using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
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
public sealed class AgentTools(RadioSession session)
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
          {"type":"function","function":{"name":"listen","description":"Demodulate one frequency for a few seconds. mode is auto, nbfm, wbfm, am, usb, lsb, cw, ft8, or dmr. CW returns Morse. FT8 uses 15 seconds. AM, FM, and sideband return a speech transcript. DMR voice is not decoded.","parameters":{"type":"object","properties":{"frequencyHz":{"type":"number"},"mode":{"type":"string"},"seconds":{"type":"number"}},"required":["frequencyHz"]}}},
          {"type":"function","function":{"name":"stop","description":"Cancel the scan or listen in progress.","parameters":{"type":"object","properties":{}}}}
        ]
        """)!.AsArray();

    public static bool Known(string name) =>
        name is "list_devices" or "open_device" or "close_device" or "get_status" or "get_capabilities"
            or "list_ham_bands" or "get_antenna" or "set_antenna" or "guess_antenna"
            or "scan_band" or "inspect_signal" or "listen" or "stop";

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
                case "stop":
                    session.Stop();
                    return Text(Json(session.Status), "Stopped.");
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
