using System.Globalization;
using System.Threading.Channels;
using AgenticSdr.Dsp;
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
        app.MapGet("/api/camp", (CampBook camp) => camp.View(DateTime.Now));
        app.MapPut("/api/camp/mode", (CampModeBody body, CampBook camp) =>
        {
            try
            {
                camp.SetMode(body.Mode);
                return Results.Ok(camp.View(DateTime.Now));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
        app.MapPost("/api/camp/entries", (CampDraft body, CampBook camp) =>
        {
            try
            {
                camp.Add(body);
                return Results.Ok(camp.View(DateTime.Now));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
        app.MapPut("/api/camp/entries/{id}", (string id, CampDraft body, CampBook camp) =>
        {
            try
            {
                camp.Update(id, body);
                return Results.Ok(camp.View(DateTime.Now));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
        app.MapDelete("/api/camp/entries/{id}", (string id, CampBook camp) =>
        {
            try
            {
                camp.Remove(id);
                return Results.Ok(camp.View(DateTime.Now));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
        app.MapPost("/api/camp/now", (CampDraft body, CampBook camp) =>
        {
            try
            {
                return Results.Ok(camp.CampNow(body, DateTime.Now));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
        app.MapDelete("/api/camp/now", (CampBook camp) =>
        {
            camp.ClearNow();
            return Results.Ok(camp.View(DateTime.Now));
        });
        app.MapPost("/api/camp/pause", (CampBook camp) =>
        {
            camp.Pause(DateTime.Now);
            return Results.Ok(camp.View(DateTime.Now));
        });
        app.MapPut("/api/camp/policy", (CampPolicyBody body, CampBook camp) =>
        {
            camp.SetYieldToSchedule(body.YieldToSchedule);
            return Results.Ok(camp.View(DateTime.Now));
        });
        app.MapPost("/api/camp/follow-schedule", (CampBook camp) =>
        {
            try
            {
                return Results.Ok(camp.FollowSchedule(DateTime.Now));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
        app.MapPost("/api/camp/ignore-schedule", (CampBook camp) =>
        {
            try
            {
                return Results.Ok(camp.IgnoreSchedule(DateTime.Now));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
        app.MapGet("/api/camp/audio", async (HttpContext http, RadioSession session, CancellationToken ct) =>
        {
            if (!session.TryJoinCamp(out ChannelReader<byte[]>? reader, out int sampleRate, out CampTune? tune) || reader is null || tune is null)
            {
                http.Response.StatusCode = 404;
                return;
            }

            http.Response.Headers["X-Audio-Rate"] = sampleRate.ToString(CultureInfo.InvariantCulture);
            http.Response.Headers["X-Tune-Hz"] = tune.FrequencyHz.ToString(CultureInfo.InvariantCulture);
            http.Response.Headers["X-Mode"] = tune.Mode;
            http.Response.ContentType = "application/octet-stream";
            http.Response.Headers.CacheControl = "no-store";
            http.Response.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();
            await http.Response.StartAsync(ct);
            try
            {
                await foreach (byte[] chunk in reader.ReadAllAsync(ct))
                    await http.Response.Body.WriteAsync(chunk, ct);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                session.LeaveCamp(reader);
            }
        });
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
        app.MapGet("/api/radio-details", (int index, RadioSession session) =>
        {
            try
            {
                return Results.Ok(session.Details(index));
            }
            catch (Exception ex) when (ex is RadioException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
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
        app.MapGet("/api/live", async (double frequencyHz, string? mode, int? colorCode, int? slot, int? talkgroup, HttpContext http, RadioSession session, CancellationToken ct) =>
        {
            try
            {
                await session.WriteLiveAsync(frequencyHz, mode, new DmrFilter(colorCode, slot, talkgroup), http.Response, ct);
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
        app.MapPost("/api/recording", (RecordingFiles files) => Results.Ok(new { id = files.Create() }));
        app.MapPost("/api/recording/{id}", async (string id, HttpContext http, RecordingFiles files, CancellationToken ct) =>
        {
            try
            {
                using var buffer = new MemoryStream();
                await http.Request.Body.CopyToAsync(buffer, ct);
                files.Append(id, buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
                return Results.NoContent();
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or IOException)
            {
                return Results.NotFound(new { error = "Recording was already removed." });
            }
        });
        app.MapMethods("/api/recording/{id}", ["DELETE"], (string id, RecordingFiles files) =>
        {
            try
            {
                files.Delete(id);
                return Results.NoContent();
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
        app.MapPost("/api/recording/{id}/discard", (string id, RecordingFiles files) =>
        {
            try
            {
                files.Delete(id);
                return Results.NoContent();
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
        app.MapGet("/api/recording/{id}", (string id, RecordingFiles files, HttpContext http) =>
        {
            try
            {
                if (!files.TryOpen(id, out Stream? stream) || stream is null)
                    return Results.NotFound(new { error = "Recording was already removed." });
                http.Response.OnCompleted(() =>
                {
                    stream.Dispose();
                    files.Delete(id);
                    return Task.CompletedTask;
                });
                return Results.File(stream, "audio/mpeg");
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
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
/// <summary>Body of PUT /api/camp/mode. Mode is explore or camp.</summary>
public sealed record CampModeBody(string? Mode);
/// <summary>Body of PUT /api/camp/policy. When true, a timed schedule entry takes the radio from an explicit camp.</summary>
public sealed record CampPolicyBody(bool YieldToSchedule);
