using AgenticSdr.Radio;

namespace AgenticSdr.Station;

/// <summary>
/// HTTP routes for the station page. The transmit route is the button.
/// It is not an agent tool, and it refuses the post unless the operator checked the box.
/// </summary>
public static class StationApi
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/station", (StationDesk desk) => desk.Snapshot());

        app.MapPost("/api/operator", (OperatorBody body, StationDesk desk, StationMemory memory) =>
        {
            try
            {
                if (!StationWords.TryParseLicense(body.License, out LicenseClass license))
                    return Results.BadRequest(new { error = "License class has to be Technician, General, or Amateur Extra." });
                desk.SignIn(body.CallSign ?? "", license, body.Grid);
                OperatorIdentity op = desk.CopyOperator()!;
                try
                {
                    memory.Save(op);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    desk.SignOut();
                    return Results.BadRequest(new { error = "Could not remember this operator for the next session. " + ex.Message });
                }

                return Results.Ok(desk.Snapshot());
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapDelete("/api/operator", (StationDesk desk, StationMemory memory) =>
        {
            try
            {
                memory.Clear();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Results.BadRequest(new { error = "Could not forget the saved operator. " + ex.Message });
            }

            desk.SignOut();
            return Results.Ok(desk.Snapshot());
        });

        app.MapPost("/api/grid/lookup", async (GridLookupBody body, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await StationGrid.LookupAsync(body.Grid, body.CallSign, ct));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapGet("/api/antenna", (StationAntenna antenna) => antenna.Current());

        app.MapPost("/api/antenna", (AntennaBody body, StationAntenna antenna) =>
        {
            try
            {
                return Results.Ok(antenna.Set(body.Length, body.Unit, body.Orientation, body.AzimuthDegrees));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/antenna/guess", async (StationAntenna antenna, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await antenna.GuessAsync(ct));
            }
            catch (OperationCanceledException)
            {
                return Results.BadRequest(new { error = "Antenna guess stopped." });
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or RadioException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        MapReceive(app);

        app.MapGet("/api/privilege", (double frequencyHz, string mode, double watts, StationDesk desk) =>
        {
            OperatorIdentity? op = desk.CopyOperator();
            if (op == null)
                return Results.BadRequest(new { error = "Set your call sign and license class first." });
            if (!StationWords.TryParseMode(mode, out StationMode parsed))
                return Results.BadRequest(new { error = "Unknown mode." });
            return Results.Ok(Part97.Evaluate(op.License, parsed, frequencyHz, watts));
        });

        app.MapGet("/api/repeater/catalog", () => StationRepeater.Catalog());

        app.MapGet("/api/repeater", (RepeaterMemory memory) => Results.Ok(memory.Book()));

        app.MapPost("/api/repeater", (SavedRepeaterBody body, RepeaterMemory memory) =>
        {
            try
            {
                if (body.Repeater is null)
                    return Results.BadRequest(new { error = "Describe the repeater first." });
                RepeaterAccess access = StationRepeater.Parse(body.Repeater);
                return Results.Ok(memory.Create(access, body.Label, body.PowerWatts));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Results.BadRequest(new { error = "Could not save that repeater. " + ex.Message });
            }
        });

        app.MapPut("/api/repeater/{id}", (string id, SavedRepeaterBody body, RepeaterMemory memory) =>
        {
            try
            {
                if (body.Repeater is null)
                    return Results.BadRequest(new { error = "Describe the repeater first." });
                RepeaterAccess access = StationRepeater.Parse(body.Repeater);
                return Results.Ok(memory.Update(id, access, body.Label, body.PowerWatts));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Results.BadRequest(new { error = "Could not update that repeater. " + ex.Message });
            }
        });

        app.MapDelete("/api/repeater/{id}", (string id, RepeaterMemory memory) =>
        {
            try
            {
                return Results.Ok(memory.Delete(id));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Results.BadRequest(new { error = "Could not delete that repeater. " + ex.Message });
            }
        });

        app.MapPut("/api/repeater/selected", (RepeaterSelectBody body, RepeaterMemory memory) =>
        {
            try
            {
                return Results.Ok(memory.Select(body.Id));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Results.BadRequest(new { error = "Could not select that repeater. " + ex.Message });
            }
        });

        app.MapPost("/api/repeater/resolve", (RepeaterBody body) =>
        {
            try
            {
                RepeaterAccess access = StationRepeater.Parse(body);
                return Results.Ok(access);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/repeater/privilege", (RepeaterPrivilegeBody body, StationDesk desk) =>
        {
            try
            {
                OperatorIdentity? op = desk.CopyOperator();
                if (op == null)
                    return Results.BadRequest(new { error = "Set your call sign and license class first." });
                if (body.Repeater is null)
                    return Results.BadRequest(new { error = "Describe the repeater first." });
                RepeaterAccess access = StationRepeater.Parse(body.Repeater);
                return Results.Ok(StationRepeater.Evaluate(op.License, access, body.PowerWatts));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/drafts", (DraftBody body, StationDesk desk, RepeaterMemory memory) =>
        {
            try
            {
                if (!StationWords.TryParseMode(body.Mode, out StationMode mode))
                    return Results.BadRequest(new { error = "Unknown mode." });
                RepeaterAccess? repeater = body.Repeater is null ? null : StationRepeater.Parse(body.Repeater);
                DraftView draft = desk.Draft(body.Text ?? "", body.FrequencyHz, mode, body.PowerWatts, DraftSource.Operator, repeater);
                if (repeater is not null)
                {
                    try
                    {
                        memory.RememberUse(repeater, body.PowerWatts);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // The draft still holds; remembering the form is optional.
                    }
                }

                return Results.Ok(draft);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapDelete("/api/drafts/{id}", (string id, StationDesk desk) =>
        {
            try
            {
                desk.Discard(id);
                return Results.Ok(desk.Snapshot());
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/heard", (HeardBody body, StationDesk desk) =>
        {
            try
            {
                StationMode? mode = null;
                if (!string.IsNullOrWhiteSpace(body.Mode))
                {
                    if (!StationWords.TryParseMode(body.Mode, out StationMode parsed))
                        return Results.BadRequest(new { error = "Unknown mode." });
                    mode = parsed;
                }

                HeardNote note = desk.NoteHeard(body.Text ?? "", body.FrequencyHz, mode, DraftSource.Operator);
                return Results.Ok(note);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/transmit", (TransmitBody body, StationDesk desk) =>
        {
            if (!OperatorClick.TryFromButton(body.DraftId, body.Authorized, out OperatorClick click))
            {
                return Results.BadRequest(new
                {
                    error = "Transmit runs only when you check the box and click Transmit. A draft is not a transmission."
                });
            }

            AuthorizeResult result = desk.Authorize(click.DraftId, click);
            return result.Authorized ? Results.Ok(result) : Results.BadRequest(result);
        });

        app.MapGet("/api/authorized/{id}/audio", (string id, double? audioHz, StationDesk desk) =>
        {
            AuthorizedTransmission? sent = desk.FindAuthorized(id);
            if (sent is null)
                return Results.NotFound(new { error = "That authorized message is no longer on the desk." });
            if (!StationSend.Supports(sent.Mode))
                return Results.BadRequest(new { error = StationSend.UnsupportedReason(sent.Mode) });

            try
            {
                double hz = audioHz is { } value && double.IsFinite(value) ? value : StationSend.DefaultAudioHz;
                SendAudio audio = StationSend.Render(sent, hz);
                return Results.File(audio.Wav, "audio/wav", fileDownloadName: $"{sent.Mode.ToString().ToLowerInvariant()}-{sent.Id}.wav");
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapGet("/api/authorized/{id}/send", (string id, double? audioHz, StationDesk desk) =>
        {
            AuthorizedTransmission? sent = desk.FindAuthorized(id);
            if (sent is null)
                return Results.NotFound(new { error = "That authorized message is no longer on the desk." });
            if (!StationSend.Supports(sent.Mode))
                return Results.BadRequest(new { error = StationSend.UnsupportedReason(sent.Mode), canSend = false });

            try
            {
                double hz = audioHz is { } value && double.IsFinite(value) ? value : StationSend.DefaultAudioHz;
                SendAudio audio = StationSend.Render(sent, hz);
                return Results.Ok(new
                {
                    canSend = true,
                    radiated = false,
                    id = audio.TransmissionId,
                    mode = audio.Mode,
                    sampleRate = audio.SampleRate,
                    durationSeconds = audio.DurationSeconds,
                    summary = audio.Summary,
                    audioUrl = $"/api/authorized/{Uri.EscapeDataString(id)}/audio?audioHz={hz:0}"
                });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message, canSend = false });
            }
        });
    }

    static void MapReceive(WebApplication app)
    {
        app.MapGet("/api/receive/status", (HttpContext http, StationReceiver receiver, CancellationToken ct) =>
            receiver.ProxyJsonAsync(http, "/api/status", HttpMethod.Get, ct));
        app.MapGet("/api/receive/devices", (HttpContext http, StationReceiver receiver, CancellationToken ct) =>
            receiver.ProxyJsonAsync(http, "/api/devices", HttpMethod.Get, ct));
        app.MapPost("/api/receive/open", (HttpContext http, StationReceiver receiver, CancellationToken ct) =>
            receiver.ProxyJsonAsync(http, "/api/open", HttpMethod.Post, ct));
        app.MapPost("/api/receive/close", (HttpContext http, StationReceiver receiver, CancellationToken ct) =>
            receiver.ProxyJsonAsync(http, "/api/close", HttpMethod.Post, ct));
        app.MapPost("/api/receive/stop", (HttpContext http, StationReceiver receiver, CancellationToken ct) =>
            receiver.ProxyJsonAsync(http, "/api/stop", HttpMethod.Post, ct));
        app.MapGet("/api/receive/processors", (HttpContext http, StationReceiver receiver, CancellationToken ct) =>
            receiver.ProxyJsonAsync(http, "/api/processors", HttpMethod.Get, ct));
        app.MapGet("/api/receive/pipelines/default", (HttpContext http, StationReceiver receiver, CancellationToken ct) =>
        {
            string query = http.Request.QueryString.HasValue ? http.Request.QueryString.Value! : "";
            return receiver.ProxyJsonAsync(http, "/api/pipelines/default" + query, HttpMethod.Get, ct);
        });
        app.MapGet("/api/receive/live", (HttpContext http, StationReceiver receiver, CancellationToken ct) =>
            receiver.ProxyLiveAsync(http, ct));
    }

    public sealed record OperatorBody(string? CallSign, string? License, string? Grid);
    public sealed record GridLookupBody(string? Grid, string? CallSign);
    public sealed record AntennaBody(double Length, string? Unit, string? Orientation, double? AzimuthDegrees);
    public sealed record DraftBody(string? Text, double FrequencyHz, string? Mode, double PowerWatts, RepeaterBody? Repeater);
    public sealed record RepeaterPrivilegeBody(RepeaterBody? Repeater, double PowerWatts);
    public sealed record RepeaterSelectBody(string? Id);
    public sealed record HeardBody(string? Text, double? FrequencyHz, string? Mode);
    public sealed record TransmitBody(string? DraftId, bool Authorized);
}
