using System.Globalization;
using AgenticSdr.Privilege;
using AgenticSdr.Radio;

namespace AgenticSdr.Cw;

public static class CwApi
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/operator", (OperatorSession session) =>
            Results.Ok(OperatorPayload(session.Current)));

        app.MapPost("/api/operator", (OperatorBody body, OperatorSession session) =>
        {
            try
            {
                if (!AmateurWords.TryParseLicense(body.License, out LicenseClass license))
                    return Results.BadRequest(new { error = "License must be Technician, General, or Extra." });
                OperatorIdentity identity = session.SignIn(body.CallSign ?? "", license, body.Grid);
                return Results.Ok(OperatorPayload(identity));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapDelete("/api/operator", (OperatorSession session) =>
        {
            session.SignOut();
            return Results.Ok(OperatorPayload(null));
        });

        app.MapPost("/api/grid/lookup", () =>
            Results.BadRequest(new { error = "Grid lookup is not wired on the CW desk yet. Enter a Maidenhead square if you want it saved with the operator." }));

        app.MapGet("/api/antenna", (AntennaStore antennas) =>
            Results.Ok(AntennaSettings.ToAdvice(antennas.Load())));

        app.MapPost("/api/antenna", (AntennaBody body, AntennaStore antennas) =>
        {
            try
            {
                AntennaSpec spec = AntennaSettings.Parse(body.Length, body.Unit, body.Orientation, body.AzimuthDegrees);
                antennas.Save(spec);
                return Results.Ok(AntennaSettings.ToAdvice(spec));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/antenna/guess", () =>
            Results.BadRequest(new { error = "Antenna guess needs the receiver on port 5088. Set length and orientation here; they are shared with Host." }));

        app.MapGet("/api/devices", (CwRadioHolder radio) =>
        {
            try
            {
                return Results.Ok(radio.ListDevices());
            }
            catch (RadioException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapGet("/api/radio/preference", (CwRadioHolder radio) => Results.Ok(radio.Preference()));

        app.MapGet("/api/radio/simulate-transmit", (CwRadioHolder radio) =>
            Results.Ok(new { enabled = radio.SimulateTransmit, simulateTransmit = radio.SimulateTransmit }));

        app.MapPost("/api/radio/simulate-transmit", (SimulateTransmitBody body, CwRadioHolder radio) =>
            Results.Ok(radio.SetSimulateTransmit(body.Enabled)));

        app.MapGet("/api/status", (CwRadioHolder radio) => Results.Ok(radio.Status()));
        app.MapGet("/api/radio", (CwRadioHolder radio) => Results.Ok(radio.Status()));
        app.MapGet("/api/chat", (CwChat chat) => Results.Ok(new { messages = chat.Snapshot() }));
        app.MapGet("/api/identity", (CwIdTracker ids) => Results.Ok(ids.Status()));

        app.MapPost("/api/listen", (ListenBody body, CwListener listener) =>
        {
            if (body.FrequencyHz is > 0)
                listener.SetFrequency(body.FrequencyHz.Value);
            if (body.Enabled == false)
                listener.Stop();
            else
                listener.Start();
            return Results.Ok(listener.Status());
        });

        app.MapGet("/api/listen/audio", async (HttpContext http, CwListener listener, CancellationToken ct) =>
        {
            if (!listener.TryAudioRate(out int sampleRate))
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                await http.Response.WriteAsJsonAsync(new { error = "Listening has not started yet." }, ct);
                return;
            }

            // Same continuous stream shape as Host /api/live: PCM chunks flushed to this body.
            http.Response.Headers["X-Audio-Rate"] = sampleRate.ToString(CultureInfo.InvariantCulture);
            http.Response.Headers["X-Audio-Channels"] = "1";
            http.Response.ContentType = "application/octet-stream";
            http.Response.Headers.CacheControl = "no-store";
            http.Response.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();
            await http.Response.StartAsync(ct);
            try
            {
                await listener.StreamAudioAsync(http.Response.Body, ct);
            }
            catch (InvalidOperationException ex)
            {
                if (!http.Response.HasStarted)
                {
                    http.Response.StatusCode = StatusCodes.Status404NotFound;
                    await http.Response.WriteAsJsonAsync(new { error = ex.Message }, ct);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
        app.MapPost("/api/signoff/preview", (SignOffBody body, OperatorSession session, CwIdTracker ids) =>
        {
            OperatorIdentity? op = session.Current;
            if (op is null)
                return Results.BadRequest(new { error = "Sign in as the control operator first." });
            try
            {
                string prosign = CwIdTracker.NormalizeProsign(body.Prosign);
                string text = ids.BuildSignOff(op, prosign);
                return Results.Ok(new
                {
                    text,
                    prosign,
                    reason = body.Reason ?? SignOffReason(prosign)
                });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/open", (OpenBody? body, CwRadioHolder radio) =>
        {
            try
            {
                return Results.Ok(radio.Open(body?.Index ?? 0));
            }
            catch (Exception ex) when (ex is RadioException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/stop", (CwListener listener) =>
        {
            listener.PauseForTransmit();
            return Results.Ok(new { stopped = true });
        });
        app.MapPost("/api/close", (CwRadioHolder radio) => Results.Ok(radio.Close()));

        app.MapGet("/api/radio-details", (int index, CwRadioHolder radio) =>
        {
            try
            {
                return Results.Ok(radio.Details(index));
            }
            catch (Exception ex) when (ex is RadioException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapGet("/api/privilege", (double frequencyHz, double watts, OperatorSession session) =>
        {
            OperatorIdentity? op = session.Current;
            if (op is null)
                return Results.BadRequest(new { error = "Sign in as the control operator first." });
            return Results.Ok(Part97.Evaluate(op.License, AmateurMode.Cw, frequencyHz, watts));
        });

        app.MapPost("/api/transmit", async (TransmitBody body, HttpContext http, OperatorSession session, CwRadioHolder holder, CwListener listener, CwChat chat, CwIdTracker ids, CancellationToken ct) =>
        {
            if (!OperatorClick.TryFromButton(body.RequestId, body.Authorized, out OperatorClick click))
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                await http.Response.WriteAsJsonAsync(new { error = "Transmit only happens when an authorized operator clicks the button." }, ct);
                return;
            }

            OperatorIdentity? op = session.Current;
            if (op is null)
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                await http.Response.WriteAsJsonAsync(new { error = "Sign in as the control operator first." }, ct);
                return;
            }

            string text;
            bool requireIdInMessage;
            if (!string.IsNullOrWhiteSpace(body.Prosign))
            {
                try
                {
                    text = ids.BuildSignOff(op, body.Prosign);
                }
                catch (ArgumentException ex)
                {
                    http.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await http.Response.WriteAsJsonAsync(new { error = ex.Message }, ct);
                    return;
                }

                requireIdInMessage = true;
            }
            else
            {
                text = ids.PrepareOutgoing(body.Text, op);
                // Fresh ID within 10 minutes: short traffic may omit the call. Otherwise the text must identify.
                requireIdInMessage = !(ids.HasFreshId && !CallSign.IsIdentified(text, op.BaseCall));
                if (requireIdInMessage && !CallSign.IsIdentified(text, op.BaseCall))
                {
                    http.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await http.Response.WriteAsJsonAsync(new { error = $"Identification is due — include {op.BaseCall}." }, ct);
                    return;
                }
            }

            var request = new CwTransmitRequest(
                text,
                body.FrequencyHz,
                body.PowerWatts,
                body.ToneHz is > 0 ? body.ToneHz.Value : CwAudio.DefaultToneHz);

            listener.SetFrequency(body.FrequencyHz);
            listener.PauseForTransmit();
            CwTransmitPlan plan;
            try
            {
                plan = CwTransmitter.Plan(op, click, request, holder.Radio, requireIdInMessage);
                if (!plan.Ok)
                {
                    http.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await http.Response.WriteAsJsonAsync(new { error = plan.Reason, radiated = false }, ct);
                    return;
                }

                ids.NoteTransmitted(text, op);
                chat.AddTx(text);

                bool radiates = plan.MayRadiate
                    && holder.Radio is IRadioTransmitter transmitter
                    && transmitter.Radiates;
                string reason = plan.LocalPreview
                    ? plan.Reason + " Local preview only; nothing radiated."
                    : plan.MayRadiate && !radiates
                        ? plan.Reason + " Simulated transmit — nothing radiated."
                        : plan.Reason;
                string summary = (plan.AudioSummary ?? "CW")
                    + (plan.LocalPreview ? " · local preview" : radiates ? " · radiated" : plan.MayRadiate ? " · simulated" : "");

                http.Response.Headers["X-Audio-Rate"] = CwAudio.SampleRate.ToString(CultureInfo.InvariantCulture);
                http.Response.Headers["X-Audio-Channels"] = "1";
                http.Response.Headers["X-Tx-Ok"] = "1";
                http.Response.Headers["X-Tx-Text"] = Uri.EscapeDataString(text);
                http.Response.Headers["X-Tx-Reason"] = Uri.EscapeDataString(reason);
                http.Response.Headers["X-Tx-Summary"] = Uri.EscapeDataString(summary);
                http.Response.Headers["X-Radiated"] = radiates ? "1" : "0";
                http.Response.ContentType = "application/octet-stream";
                http.Response.Headers.CacheControl = "no-store";
                http.Response.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();
                await http.Response.StartAsync(ct);
                await CwTransmitter.PlayAsync(plan, holder.Radio, http.Response.Body, ct);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                listener.EndTransmit();
            }
        });

        app.MapGet("/api/audio/{id}", (string id, CwRadioHolder holder) =>
        {
            if (!holder.TryTakePreview(id, out byte[]? wav) || wav is null)
                return Results.NotFound();
            return Results.File(wav, "audio/wav");
        });

        app.MapGet("/api/ui-prefs", (UiPrefsStore prefs) => Results.Ok(prefs.Load()));
        app.MapPut("/api/ui-prefs", (UiPrefsPatch body, UiPrefsStore prefs) =>
        {
            try
            {
                return Results.Ok(prefs.Patch(body ?? new UiPrefsPatch()));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapGet("/api/llm", (LlmStore store) => store.View());
        app.MapPut("/api/llm", (LlmSave body, LlmStore store) =>
        {
            try
            {
                return Results.Ok(store.Save(body));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
        app.MapGet("/api/llm/models", async (CwAgent agent, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await agent.ListModelsAsync(ct));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
        app.MapPost("/api/agent", async (CwAgentBody body, CwAgent agent, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await agent.AskAsync(body.Message, body.History, ct));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
        app.MapPost("/api/cw/translate", (CwTranslateBody body) =>
            Results.Ok(CwJargon.Explain(body.Text, body.Direction)));
    }

    public sealed record OperatorBody(string? CallSign, string? License, string? Grid);
    public sealed record AntennaBody(double Length, string? Unit, string? Orientation, double? AzimuthDegrees);
    public sealed record OpenBody(int Index);
    public sealed record SimulateTransmitBody(bool Enabled);
    public sealed record CwAgentBody(string? Message, IReadOnlyList<CwAgentHistoryTurn>? History);
    public sealed record CwTranslateBody(string? Text, string? Direction);
    public sealed record ListenBody(double? FrequencyHz, bool? Enabled);
    public sealed record SignOffBody(string? Prosign, string? Reason);
    public sealed record TransmitBody(
        string? RequestId,
        bool Authorized,
        string? Text,
        double FrequencyHz,
        double PowerWatts,
        double? ToneHz,
        string? Prosign);

    static string SignOffReason(string prosign) => prosign switch
    {
        "QSY" => "Changing frequency",
        "QRT" => "Going off the air",
        "CL" => "Closing the station",
        "SK" => "Ending the contact",
        _ => "Sign off"
    };

    static object OperatorPayload(OperatorIdentity? op) => new
    {
        @operator = op,
        operatorIdentity = op,
        licenseSummary = op is null ? null : Part97.Summary(op.License)
    };
}
