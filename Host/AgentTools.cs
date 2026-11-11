using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AgenticSdr.Dsp;
using AgenticSdr.Radio;

namespace AgenticSdr.Host;

/// <summary>One action the instruct model asked for, plus a short result the page can show.</summary>
public sealed record AgentAction(string Tool, string Arguments, string Result);

/// <summary>
/// The model's reply. Scan and Antenna are set when a tool changed those, so the page can update.
/// ListenText is Morse, FT8, or the DMR note from a listen the model requested.
/// </summary>
public sealed record AgentReply(
    string Answer,
    IReadOnlyList<AgentAction> Actions,
    ScanReport? Scan,
    AntennaAdvice? Antenna,
    string? ListenText);

/// <summary>A prior user or assistant sentence. Tool results are not accepted from the browser.</summary>
public sealed record AgentHistoryTurn(string? Role, string? Content);

/// <summary>
/// The radio actions an instruct model may request. They are the same actions as the buttons and the MCP tools.
/// The model does not tune the radio itself. It names a tool, and this class runs it.
/// </summary>
public sealed class AgentTools(RadioSession session, CampBook camp, ClipStore clips)
{
    public static readonly JsonArray Schema = JsonNode.Parse(
        """
        [
          {"type":"function","function":{"name":"list_devices","description":"List USB SDRs currently visible.","parameters":{"type":"object","properties":{}}}},
          {"type":"function","function":{"name":"open_device","description":"Open a device by index from list_devices. Index 0 is the first radio.","parameters":{"type":"object","properties":{"index":{"type":"integer"}}}}},
          {"type":"function","function":{"name":"close_device","description":"Close the open SDR so another program can use it.","parameters":{"type":"object","properties":{}}}},
          {"type":"function","function":{"name":"get_status","description":"Whether a radio is open and what it is doing right now.","parameters":{"type":"object","properties":{}}}},
          {"type":"function","function":{"name":"get_capabilities","description":"Tuning range, sample rate, gain, and whether HF uses direct sampling.","parameters":{"type":"object","properties":{}}}},
          {"type":"function","function":{"name":"list_ham_bands","description":"List amateur bands plus AM, FM, NOAA, CB, GMRS, and DMR. Recommended bands come first when an antenna is set.","parameters":{"type":"object","properties":{}}}},
          {"type":"function","function":{"name":"get_antenna","description":"The current antenna and the bands it matches.","parameters":{"type":"object","properties":{}}}},
          {"type":"function","function":{"name":"set_antenna","description":"Set antenna length and orientation. Vertical is a quarter-wave. Horizontal is a half-wave and needs azimuthDegrees, degrees true, 0 meaning north.","parameters":{"type":"object","properties":{"length":{"type":"number"},"unit":{"type":"string","description":"m or ft"},"orientation":{"type":"string","description":"vertical or horizontal"},"azimuthDegrees":{"type":"number"}},"required":["length"]}}},
          {"type":"function","function":{"name":"guess_antenna","description":"Guess the antenna from where the noise floor is strongest. Compass direction cannot be read this way.","parameters":{"type":"object","properties":{}}}},
          {"type":"function","function":{"name":"scan_band","description":"Scan one band. bandId examples: 2m, 70cm, 40m, fm-bc, am-bc, noaa, cb, gmrs, dmr.","parameters":{"type":"object","properties":{"bandId":{"type":"string"}},"required":["bandId"]}}},
          {"type":"function","function":{"name":"inspect_signal","description":"Measure one frequency in Hz.","parameters":{"type":"object","properties":{"frequencyHz":{"type":"number"}},"required":["frequencyHz"]}}},
          {"type":"function","function":{"name":"listen","description":"Demodulate one frequency for a few seconds. mode is auto, nbfm, wbfm, am, usb, lsb, cw, ft8, js8, olivia, jtty, varac, dmr, or packet. CW returns Morse. Digimodes return text when decoded. Scan activity is not a decode.","parameters":{"type":"object","properties":{"frequencyHz":{"type":"number"},"mode":{"type":"string"},"seconds":{"type":"number"}},"required":["frequencyHz"]}}},
          {"type":"function","function":{"name":"list_clips","description":"List recent listen clips with clipId values for analyze_clip.","parameters":{"type":"object","properties":{"limit":{"type":"integer"}}}}},
          {"type":"function","function":{"name":"analyze_clip","description":"Diagnose a digimode listen WAV: energy width, FT8 Costas/LDPC, JS8 count, VarAC status, decoder self-test, and hints. Pass clipId from listen, or omit for the latest clip. Does not retune.","parameters":{"type":"object","properties":{"clipId":{"type":"string"},"mode":{"type":"string","description":"Optional ft8, js8, or varac hint"}}}}},
          {"type":"function","function":{"name":"stop","description":"Cancel the scan or listen in progress. In camp mode this pauses the schedule until Listen or Camp is chosen again.","parameters":{"type":"object","properties":{}}}},
          {"type":"function","function":{"name":"set_operating_mode","description":"Choose explore or camp. Explore scans and listens with the audio on. Camp tunes the schedule and keeps the audio off until the channel is active.","parameters":{"type":"object","properties":{"mode":{"type":"string","description":"explore or camp"}},"required":["mode"]}}},
          {"type":"function","function":{"name":"get_schedule","description":"The camp schedule, the current mode, and which entry would use the radio now.","parameters":{"type":"object","properties":{}}}},
          {"type":"function","function":{"name":"add_schedule_entry","description":"Add a camp entry. frequencyHz is hertz. mode is nbfm, wbfm, am, usb, lsb, cw, ft8, dmr, or raw. start and end are local HH:mm; omit both to listen whenever no timed entry is on. days is sun, mon, weekdays, weekends, or empty for every day. Overlapping times are rejected.","parameters":{"type":"object","properties":{"frequencyHz":{"type":"number"},"mode":{"type":"string"},"start":{"type":"string"},"end":{"type":"string"},"days":{"type":"string"},"label":{"type":"string"},"colorCode":{"type":"integer"},"slot":{"type":"integer"},"talkgroup":{"type":"integer"}},"required":["frequencyHz","mode"]}}},
          {"type":"function","function":{"name":"update_schedule_entry","description":"Replace one schedule entry by id. Send the full entry. Omit start and end to make it an open watch. Overlaps are rejected.","parameters":{"type":"object","properties":{"id":{"type":"string"},"frequencyHz":{"type":"number"},"mode":{"type":"string"},"start":{"type":"string"},"end":{"type":"string"},"days":{"type":"string"},"label":{"type":"string"},"colorCode":{"type":"integer"},"slot":{"type":"integer"},"talkgroup":{"type":"integer"}},"required":["id","frequencyHz","mode"]}}},
          {"type":"function","function":{"name":"remove_schedule_entry","description":"Remove one schedule entry by id.","parameters":{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}}},
          {"type":"function","function":{"name":"camp_now","description":"Camp on this frequency and mode right away. The schedule is optional. Audio stays off until the channel is active.","parameters":{"type":"object","properties":{"frequencyHz":{"type":"number"},"mode":{"type":"string"},"label":{"type":"string"},"colorCode":{"type":"integer"},"slot":{"type":"integer"},"talkgroup":{"type":"integer"}},"required":["frequencyHz","mode"]}}},
          {"type":"function","function":{"name":"clear_camp_now","description":"Clear the frequency chosen with camp_now. Timed schedule entries stay.","parameters":{"type":"object","properties":{}}}},
          {"type":"function","function":{"name":"set_schedule_policy","description":"While camping on an explicit frequency: yieldToSchedule true means a timed schedule entry takes the radio; false means stay on that frequency and ignore the schedule until asked otherwise.","parameters":{"type":"object","properties":{"yieldToSchedule":{"type":"boolean"}},"required":["yieldToSchedule"]}}},
          {"type":"function","function":{"name":"follow_schedule","description":"While camping on an explicit frequency, switch to the timed schedule entry that is due right now. Returns when that window ends if camp_now is still set.","parameters":{"type":"object","properties":{}}}},
          {"type":"function","function":{"name":"ignore_schedule","description":"While camping on an explicit frequency, keep that frequency through the timed schedule entry that is due right now.","parameters":{"type":"object","properties":{}}}}
        ]
        """)!.AsArray();

    public static bool Known(string name) =>
        name is "list_devices" or "open_device" or "close_device" or "get_status" or "get_capabilities"
            or "list_ham_bands" or "get_antenna" or "set_antenna" or "guess_antenna"
            or "scan_band" or "inspect_signal" or "listen" or "list_clips" or "analyze_clip" or "stop"
            or "set_operating_mode" or "get_schedule" or "add_schedule_entry" or "update_schedule_entry"
            or "remove_schedule_entry" or "camp_now" or "clear_camp_now"
            or "set_schedule_policy" or "follow_schedule" or "ignore_schedule";

    public async Task<ToolOutcome> RunAsync(string name, string argumentsJson, CancellationToken cancellationToken)
    {
        JsonObject args = ParseArgs(argumentsJson);
        try
        {
            switch (name)
            {
                case "list_devices":
                    return Text(Json(RadioSession.ListDevices()), "Listed the radios.");
                case "open_device":
                    await session.OpenAsync(IntArg(args, "index") ?? 0, cancellationToken);
                    return Text(Json(session.Status), session.Status.Message ?? "Radio started.");
                case "close_device":
                    session.Close();
                    return Text(Json(session.Status), session.Status.Message ?? "Radio is stopped.");
                case "get_status":
                    return Text(Json(session.Status), session.Status.Message ?? session.Status.Phase);
                case "get_capabilities":
                    return Text(Json(session.Capabilities()), "Read the radio's range.");
                case "list_ham_bands":
                {
                    IReadOnlyList<BandListItem> bands = session.ListBands();
                    return Text(Json(bands), $"{bands.Count} bands.");
                }
                case "get_antenna":
                {
                    AntennaAdvice advice = session.Antenna();
                    return new ToolOutcome(Json(advice), Summary: advice.Summary, Antenna: advice);
                }
                case "set_antenna":
                {
                    AntennaAdvice advice = session.SetAntenna(
                        Number(args, "length"),
                        TextArg(args, "unit"),
                        TextArg(args, "orientation") ?? "vertical",
                        NumberOrNull(args, "azimuthDegrees"));
                    return new ToolOutcome(Json(advice), Summary: advice.Summary, Antenna: advice);
                }
                case "guess_antenna":
                {
                    AntennaAdvice advice = await session.GuessAntennaAsync(cancellationToken);
                    return new ToolOutcome(Json(advice), Summary: advice.Summary, Antenna: advice);
                }
                case "scan_band":
                {
                    ScanReport report = await session.ScanAsync(TextArg(args, "bandId") ?? "", cancellationToken);
                    string summary = report.Hits.Count == 1 ? "1 signal." : $"{report.Hits.Count} signals.";
                    if (!string.IsNullOrWhiteSpace(report.Warning))
                        summary += " " + report.Warning;
                    return new ToolOutcome(Json(report), Summary: summary, Scan: report);
                }
                case "inspect_signal":
                {
                    InspectReport report = await session.InspectAsync(Number(args, "frequencyHz"), cancellationToken);
                    return Text(Json(report), $"{report.FrequencyHz / 1e6:0.000} MHz {report.Mode}, {report.Source}.");
                }
                case "listen":
                {
                    ListenReport report = await session.ListenAsync(
                        Number(args, "frequencyHz"),
                        TextArg(args, "mode") ?? "auto",
                        NumberOrNull(args, "seconds") ?? 8,
                        cancellationToken);
                    string heard = report.Text ?? report.Morse ?? $"{report.Seconds:0} s of {report.Mode}.";
                    return new ToolOutcome(Json(report), Summary: heard, ListenText: report.Text ?? report.Morse);
                }
                case "list_clips":
                {
                    int limit = IntArg(args, "limit") ?? 8;
                    var listed = clips.Recent(limit).Select(clip => new
                    {
                        clipId = clip.Id,
                        frequencyHz = clip.FrequencyHz,
                        mode = clip.Mode,
                        hasText = !string.IsNullOrWhiteSpace(clip.Morse),
                        audioUri = $"audio://clips/{clip.Id}"
                    }).ToList();
                    return Text(Json(listed), listed.Count == 1 ? "1 clip." : $"{listed.Count} clips.");
                }
                case "analyze_clip":
                {
                    string? id = TextArg(args, "clipId");
                    AudioClip? clip = string.IsNullOrWhiteSpace(id) || id.Equals("latest", StringComparison.OrdinalIgnoreCase)
                        ? clips.Latest()
                        : clips.TryGet(id, out AudioClip? found) ? found : null;
                    if (clip is null)
                        throw new ArgumentException(string.IsNullOrWhiteSpace(id)
                            ? "No listen clips yet. Call listen first."
                            : $"No audio clip '{id}'.");
                    string hint = TextArg(args, "mode") ?? clip.Mode;
                    DigiAnalysisReport report = DigiClipAnalyzer.AnalyzeWav(clip.Wav, hint);
                    var payload = new
                    {
                        clipId = clip.Id,
                        frequencyHz = clip.FrequencyHz,
                        mode = clip.Mode,
                        report.SampleRate,
                        report.DurationSeconds,
                        report.Rms,
                        report.Peak,
                        report.HotBins200To3000,
                        report.DominantPeakHz,
                        report.DominantWidthHz,
                        report.ToneEntropy,
                        report.Ft8Count,
                        report.Ft8BestSync,
                        report.Ft8LdpcOk,
                        report.Js8Count,
                        report.VaracMonitorAvailable,
                        report.VaracStatus,
                        report.SelfTestOk,
                        report.Messages,
                        report.Hints
                    };
                    string summary = report.Messages.Count > 0
                        ? $"Decoded {report.Messages.Count} digi line(s)."
                        : (report.Hints.Count > 0 ? report.Hints[0] : "No digi text in that clip.");
                    return Text(Json(payload), summary);
                }
                case "stop":
                    if (camp.Mode == "camp")
                        camp.Pause(DateTime.Now);
                    session.Stop();
                    return Text(Json(session.Status), camp.Mode == "camp" ? "Camp paused." : "Stopped.");
                case "set_operating_mode":
                    camp.SetMode(TextArg(args, "mode"));
                    return Text(Json(camp.View(DateTime.Now)), camp.Mode == "camp" ? "Camp mode." : "Explore mode.");
                case "get_schedule":
                {
                    CampView view = camp.View(DateTime.Now);
                    return Text(Json(view), view.Entries.Count == 1 ? "1 schedule entry." : $"{view.Entries.Count} schedule entries.");
                }
                case "add_schedule_entry":
                {
                    CampEntry entry = camp.Add(Draft(args));
                    return Text(Json(entry), $"Added {CampSchedule.Describe(entry)}.");
                }
                case "update_schedule_entry":
                {
                    string id = TextArg(args, "id") ?? "";
                    CampEntry entry = camp.Update(id, Draft(args));
                    return Text(Json(entry), $"Updated {CampSchedule.Describe(entry)}.");
                }
                case "remove_schedule_entry":
                    camp.Remove(TextArg(args, "id") ?? "");
                    return Text(Json(camp.View(DateTime.Now)), "Removed that schedule entry.");
                case "camp_now":
                {
                    CampView view = camp.CampNow(Draft(args), DateTime.Now);
                    return Text(Json(view), view.Note ?? "Camping on that frequency.");
                }
                case "clear_camp_now":
                    camp.ClearNow();
                    return Text(Json(camp.View(DateTime.Now)), "Cleared the camp-now frequency.");
                case "set_schedule_policy":
                {
                    bool? yield = args["yieldToSchedule"] is JsonValue flag && flag.TryGetValue(out bool value) ? value : null;
                    if (yield is null)
                        throw new ArgumentException("Set yieldToSchedule to true or false.");
                    camp.SetYieldToSchedule(yield.Value);
                    CampView view = camp.View(DateTime.Now);
                    return Text(Json(view), yield.Value
                        ? "Scheduled times will take the radio."
                        : "An explicit camp keeps the radio through scheduled times.");
                }
                case "follow_schedule":
                {
                    CampView view = camp.FollowSchedule(DateTime.Now);
                    return Text(Json(view), view.Note ?? "Switched to the schedule.");
                }
                case "ignore_schedule":
                {
                    CampView view = camp.IgnoreSchedule(DateTime.Now);
                    return Text(Json(view), view.Note ?? "Staying on the camp frequency.");
                }
                default:
                    return Text("""{"error":"Unknown tool."}""", "That tool is not available.");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is RadioException or ArgumentException or InvalidOperationException)
        {
            return Text(JsonSerializer.Serialize(new { error = ex.Message }), ex.Message);
        }
    }

    private static ToolOutcome Text(string json, string summary) => new(json, summary);

    private static JsonObject ParseArgs(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new JsonObject();
        try
        {
            return JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    private static string Json(object value) =>
        JsonSerializer.Serialize(value, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() }
        });

    private static string? TextArg(JsonObject args, string name) =>
        args[name] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static int? IntArg(JsonObject args, string name)
    {
        if (args[name] is not JsonValue value)
            return null;
        if (value.TryGetValue(out int number))
            return number;
        return value.TryGetValue(out double wide) ? (int)wide : null;
    }

    private static double Number(JsonObject args, string name)
    {
        if (args[name] is JsonValue value && value.TryGetValue(out double number))
            return number;
        throw new ArgumentException($"Missing {name}.");
    }

    private static CampDraft Draft(JsonObject args) => new()
    {
        Label = TextArg(args, "label"),
        FrequencyHz = Number(args, "frequencyHz"),
        Mode = TextArg(args, "mode"),
        Start = TextArg(args, "start"),
        End = TextArg(args, "end"),
        Days = CampSchedule.ParseDays(TextArg(args, "days")),
        ColorCode = IntArg(args, "colorCode"),
        Slot = IntArg(args, "slot"),
        Talkgroup = IntArg(args, "talkgroup")
    };

    private static double? NumberOrNull(JsonObject args, string name)
    {
        if (args[name] is null)
            return null;
        if (args[name] is JsonValue value && value.TryGetValue(out double number))
            return number;
        return null;
    }
}

/// <summary>What a tool returned. Json goes back to the model. Summary is the short line on the page.</summary>
public sealed record ToolOutcome(
    string Json,
    string Summary,
    ScanReport? Scan = null,
    AntennaAdvice? Antenna = null,
    string? ListenText = null);
