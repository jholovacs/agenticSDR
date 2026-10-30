using AgenticSdr.Radio;

namespace AgenticSdr.Host;

/// <summary>
/// The HTTP routes the page calls: status, devices, antenna, bands, scan, listen, and the WAV clips.
/// Errors the radio can explain are returned as text. Anything else is left to the host.
/// </summary>
public static class WebApi
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/status", (RadioSession session) => session.Status);
        app.MapGet("/api/devices", () =>
        {
            try
            {
                return Results.Ok(RadioSession.ListDevices());
            }
            catch (RadioException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
        app.MapPost("/api/open", async (OpenBody? body, RadioSession session, CancellationToken ct) =>
            await Run(async () =>
            {
                await session.OpenAsync(body?.Index ?? 0, ct);
                return session.Status;
            }));
        app.MapPost("/api/close", (RadioSession session) =>
        {
            session.Close();
            return Results.Ok(session.Status);
        });
        app.MapGet("/api/capabilities", (RadioSession session) =>
        {
            try
            {
                return Results.Ok(session.Capabilities());
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
        app.MapGet("/api/bands", (RadioSession session) => session.ListBands());
        app.MapGet("/api/antenna", (RadioSession session) => session.Antenna());
        app.MapPost("/api/antenna/guess", async (RadioSession session, CancellationToken ct) =>
            await Run(() => session.GuessAntennaAsync(ct)));
        app.MapPost("/api/antenna", (AntennaBody body, RadioSession session) =>
        {
            try
            {
                return Results.Ok(session.SetAntenna(body.Length, body.Unit, body.Orientation, body.AzimuthDegrees));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
        app.MapPost("/api/scan", async (ScanBody body, RadioSession session, CancellationToken ct) =>
            await Run(() => session.ScanAsync(body.BandId, ct)));
        app.MapPost("/api/inspect", async (InspectBody body, RadioSession session, CancellationToken ct) =>
            await Run(() => session.InspectAsync(body.FrequencyHz, ct)));
        app.MapPost("/api/listen", async (ListenBody body, RadioSession session, CancellationToken ct) =>
            await Run(() => session.ListenAsync(body.FrequencyHz, body.Mode, body.Seconds ?? 8, ct)));
        app.MapGet("/api/live", async (double frequencyHz, string? mode, HttpContext http, RadioSession session, CancellationToken ct) =>
        {
            try
            {
                await session.WriteLiveAsync(frequencyHz, mode, http.Response, ct);
            }
            catch (OperationCanceledException)
            {
                if (!http.Response.HasStarted)
                    http.Response.StatusCode = 499;
            }
            catch (Exception ex) when (ex is RadioException or ArgumentException or InvalidOperationException)
            {
                if (http.Response.HasStarted)
                    return;
                http.Response.StatusCode = 400;
                await http.Response.WriteAsJsonAsync(new { error = ex.Message }, ct);
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
        app.MapGet("/api/llm/models", async (InstructAgent agent, CancellationToken ct) =>
            await Run(() => agent.ListModelsAsync(ct)));
        app.MapPost("/api/agent", async (AgentBody body, InstructAgent agent, CancellationToken ct) =>
            await Run(() => agent.AskAsync(body.Message, body.History, ct)));
        app.MapPost("/api/stop", (RadioSession session) =>
        {
            session.Stop();
            return Results.Ok(session.Status);
        });
        app.MapGet("/audio/{id}.wav", (string id, ClipStore clips) =>
        {
            if (!clips.TryGet(id, out AudioClip? clip) || clip is null)
                return Results.NotFound();
            return Results.File(clip.Wav, "audio/wav");
        });
    }

    private static async Task<IResult> Run<T>(Func<Task<T>> action)
    {
        try
        {
            return Results.Ok(await action());
        }
        catch (OperationCanceledException)
        {
            return Results.Json(new { error = "Stopped." }, statusCode: 499);
        }
        catch (Exception ex) when (ex is RadioException or ArgumentException or InvalidOperationException)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }
}

/// <summary>Body of POST /api/open. Index is the position from the device list.</summary>
public sealed record OpenBody(int? Index);
/// <summary>Body of POST /api/antenna. Unit is m or ft. Orientation is vertical or horizontal. Azimuth is degrees true for a horizontal wire.</summary>
public sealed record AntennaBody(double Length, string? Unit, string? Orientation, double? AzimuthDegrees);
/// <summary>Body of POST /api/scan. BandId is a value such as 2m, 40m, fm-bc, am-bc, noaa, cb, gmrs, or dmr.</summary>
public sealed record ScanBody(string BandId);
/// <summary>Body of POST /api/inspect. FrequencyHz is the frequency to measure, in hertz.</summary>
public sealed record InspectBody(double FrequencyHz);
/// <summary>Body of POST /api/listen. Mode is auto, nbfm, wbfm, am, usb, lsb, cw, or ft8. Seconds is capped at 20, and FT8 always uses 15.</summary>
public sealed record ListenBody(double FrequencyHz, string? Mode, double? Seconds);
/// <summary>Body of POST /api/agent. Message is the question. History is earlier user and assistant sentences, not tool results.</summary>
public sealed record AgentBody(string? Message, IReadOnlyList<AgentHistoryTurn>? History);
