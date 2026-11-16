using System.ComponentModel;
using AgenticSdr.Host;
using AgenticSdr.Radio;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AgenticSdr.Station;

/// <summary>
/// Tools an agent may call on the station desk. They draft and log copy.
/// There is no transmit tool. Key-up is the Transmit button on the station page, and only that button
/// can create the operator click <see cref="StationDesk.Authorize"/> requires.
/// </summary>
[McpServerToolType]
public sealed class StationTools(StationDesk desk, StationAntenna antenna)
{
    [McpServerTool, Description("The control operator signed in on the station page, or null if nobody has signed in. Does not change the operator and does not transmit.")]
    public object get_control_operator()
    {
        OperatorIdentity? op = desk.CopyOperator();
        if (op == null)
            return new { signedIn = false };
        return new
        {
            signedIn = true,
            callSign = op.CallSign,
            baseCall = op.BaseCall,
            license = StationWords.LicenseName(op.License),
            summary = Part97.Summary(op.License),
            grid = op.Grid,
            latitude = op.Latitude,
            longitude = op.Longitude,
            place = op.Place,
            coordinates = op.Coordinates
        };
    }

    [McpServerTool, Description("The antenna saved for this station, and the bands that length matches. Does not transmit.")]
    public AntennaAdvice get_antenna() => antenna.Current();

    [McpServerTool, Description("Set the antenna length and orientation. A vertical is a quarter-wave. A horizontal wire is a half-wave aimed on a true compass bearing. This is remembered for the next session. Does not transmit.")]
    public AntennaAdvice set_antenna(
        [Description("Approximate length. Use unit to say whether this number is meters or feet.")] double length,
        [Description("m or ft. Defaults to meters.")] string unit = "m",
        [Description("vertical or horizontal.")] string orientation = "vertical",
        [Description("For a horizontal wire, the direction one end points, in degrees true. 0 is north.")] double? azimuthDegrees = null) =>
        Guard(() => antenna.Set(length, unit, orientation, azimuthDegrees));

    [McpServerTool, Description("Guess the antenna from the noise floor. Uses the receiver on port 5088 when that radio is already open, otherwise listens on a connected radio. Compass direction cannot be read this way. Does not transmit.")]
    public Task<AntennaAdvice> guess_antenna(CancellationToken cancellationToken = default) =>
        GuardAsync(() => antenna.GuessAsync(cancellationToken));

    [McpServerTool, Description("Turn a Maidenhead grid into coordinates, or look up the published grid for a call sign when grid is empty. This is not a license check and it does not sign the operator in. Does not transmit.")]
    public Task<GridPlace> lookup_grid(
        [Description("A grid such as FN42 or FN42kp. Leave empty to look up callSign.")] string? grid = null,
        [Description("Call sign to look up when grid is empty.")] string? callSign = null,
        CancellationToken cancellationToken = default) =>
        GuardAsync(() => StationGrid.LookupAsync(grid, callSign, cancellationToken));

    [McpServerTool, Description("Plain-language summary of the signed-in license. Does not transmit.")]
    public string describe_license()
    {
        OperatorIdentity? op = desk.CopyOperator();
        if (op == null)
            return "No control operator is signed in. The operator enters a call sign and license class on the station page.";
        return Part97.Summary(op.License);
    }

    [McpServerTool, Description("Check one frequency, mode, and power against the signed-in license. Does not draft and does not transmit. mode is cw, usb, lsb, am, nbfm, data, ft8, js8, olivia, jtty, varac, or packet. frequencyHz is hertz. watts is PEP, or ERP on 60 m.")]
    public PrivilegeDecision explain_privilege(
        [Description("Frequency in hertz, for example 14074000.")] double frequencyHz,
        [Description("cw, usb, lsb, am, nbfm, data, ft8, js8, olivia, jtty, varac, or packet.")] string mode,
        [Description("Power in watts PEP. On 60 m, enter ERP.")] double watts = 5)
    {
        OperatorIdentity? op = desk.CopyOperator()
            ?? throw new McpException("No control operator is signed in. The operator enters a call sign and license class on the station page.");
        if (!StationWords.TryParseMode(mode, out StationMode parsed))
            throw new McpException("Unknown mode. Use cw, usb, lsb, am, nbfm, data, ft8, js8, olivia, jtty, varac, or packet.");
        return Part97.Evaluate(op.License, parsed, frequencyHz, watts);
    }

    [McpServerTool, Description("Hold an outbound message for the control operator. This does not transmit. The operator must check the box and click Transmit on the station page. The message must include the operator's call sign. Frequency is hertz. watts is PEP, or ERP on 60 m.")]
    public DraftView draft_message(
        [Description("The text to send. Include the operator's call sign.")] string text,
        [Description("Frequency in hertz.")] double frequencyHz,
        [Description("cw, usb, lsb, am, nbfm, data, ft8, js8, olivia, jtty, varac, or packet.")] string mode,
        [Description("Power in watts PEP. On 60 m, enter ERP.")] double watts = 5)
    {
        if (!StationWords.TryParseMode(mode, out StationMode parsed))
            throw new McpException("Unknown mode. Use cw, usb, lsb, am, nbfm, data, ft8, js8, olivia, jtty, varac, or packet.");
        return Guard(() => desk.Draft(text, frequencyHz, parsed, watts, DraftSource.Agent));
    }

    [McpServerTool, Description("CTCSS tones, DCS codes, standard offsets, and shift names for FM phone on a repeater. Does not look up a directory and does not transmit.")]
    public object repeater_catalog() => StationRepeater.Catalog();

    [McpServerTool, Description("Hold an FM phone draft for a repeater or simplex channel. Output is the published listen frequency. Input is computed from shift/offset, or typed for split. Encode opens the machine. Decode is optional receive squelch. Refuses machines the signed-in license cannot transmit on (Part 97 on the input). This does not transmit. The operator must click Transmit.")]
    public DraftView draft_repeater_phone(
        [Description("The text to say. Include the operator's call sign.")] string text,
        [Description("Published output / listen frequency in hertz.")] double outputFrequencyHz,
        [Description("plus, minus, simplex, or split.")] string shift = "minus",
        [Description("Offset magnitude in hertz for plus/minus. Leave empty to use the band standard.")] double? offsetHz = null,
        [Description("Transmit / input frequency in hertz when shift is split.")] double? inputFrequencyHz = null,
        [Description("none, ctcss, or dcs. Encode opens the repeater.")] string encodeMode = "none",
        [Description("Encode CTCSS in hertz, such as 100.0.")] double? encodeCtcssHz = null,
        [Description("Encode DCS code, such as 023.")] string? encodeDcs = null,
        [Description("none, ctcss, or dcs. Optional receive tone squelch.")] string decodeMode = "none",
        [Description("Decode CTCSS in hertz.")] double? decodeCtcssHz = null,
        [Description("Decode DCS code.")] string? decodeDcs = null,
        [Description("True for narrow FM (usual amateur phone).")] bool narrow = true,
        [Description("Repeater call sign, if known.")] string? repeaterCallSign = null,
        [Description("Optional name or landmark.")] string? name = null,
        [Description("Optional city.")] string? city = null,
        [Description("Optional notes.")] string? notes = null,
        [Description("Power in watts PEP.")] double watts = 5)
    {
        return Guard(() =>
        {
            RepeaterAccess access = StationRepeater.Parse(new RepeaterBody(
                outputFrequencyHz,
                inputFrequencyHz,
                shift,
                offsetHz,
                encodeMode,
                encodeCtcssHz,
                encodeDcs,
                decodeMode,
                decodeCtcssHz,
                decodeDcs,
                narrow,
                repeaterCallSign,
                name,
                city,
                notes));
            return desk.Draft(text, access.InputFrequencyHz, StationMode.Nbfm, watts, DraftSource.Agent, access);
        });
    }

    [McpServerTool, Description("Check FM phone on a repeater against the signed-in license. Privilege is judged on the transmit (input) frequency. Does not draft and does not transmit.")]
    public PrivilegeDecision explain_repeater_privilege(
        [Description("Published output / listen frequency in hertz.")] double outputFrequencyHz,
        [Description("plus, minus, simplex, or split.")] string shift = "minus",
        [Description("Offset magnitude in hertz for plus/minus.")] double? offsetHz = null,
        [Description("Transmit / input frequency in hertz when shift is split.")] double? inputFrequencyHz = null,
        [Description("none, ctcss, or dcs.")] string encodeMode = "none",
        [Description("Encode CTCSS in hertz.")] double? encodeCtcssHz = null,
        [Description("Encode DCS code.")] string? encodeDcs = null,
        [Description("none, ctcss, or dcs.")] string decodeMode = "none",
        [Description("Decode CTCSS in hertz.")] double? decodeCtcssHz = null,
        [Description("Decode DCS code.")] string? decodeDcs = null,
        [Description("True for narrow FM.")] bool narrow = true,
        [Description("Power in watts PEP.")] double watts = 5)
    {
        OperatorIdentity? op = desk.CopyOperator()
            ?? throw new McpException("No control operator is signed in. The operator enters a call sign and license class on the station page.");
        return Guard(() =>
        {
            RepeaterAccess access = StationRepeater.Parse(new RepeaterBody(
                outputFrequencyHz,
                inputFrequencyHz,
                shift,
                offsetHz,
                encodeMode,
                encodeCtcssHz,
                encodeDcs,
                decodeMode,
                decodeCtcssHz,
                decodeDcs,
                narrow,
                null,
                null,
                null,
                null));
            return StationRepeater.Evaluate(op.License, access, watts);
        });
    }

    [McpServerTool, Description("Drafts waiting for the operator, with whether each one is legal for the signed-in license. Does not transmit.")]
    public IReadOnlyList<DraftView> list_drafts() => desk.Snapshot().Drafts;

    [McpServerTool, Description("Log text the station heard. This is receive-side copy. It does not transmit and it does not authorize a reply.")]
    public HeardNote note_heard(
        [Description("What was copied.")] string text,
        [Description("Frequency in hertz, if known.")] double? frequencyHz = null,
        [Description("Optional mode: cw, usb, lsb, am, nbfm, data, ft8, js8, olivia, jtty, varac, or packet.")] string? mode = null)
    {
        StationMode? parsed = null;
        if (!string.IsNullOrWhiteSpace(mode))
        {
            if (!StationWords.TryParseMode(mode, out StationMode value))
                throw new McpException("Unknown mode.");
            parsed = value;
        }

        return Guard(() => desk.NoteHeard(text, frequencyHz, parsed, DraftSource.Agent));
    }

    [McpServerTool, Description("Copy logged at the station. Does not transmit.")]
    public IReadOnlyList<HeardNote> list_heard() => desk.Snapshot().Heard;

    [McpServerTool, Description("Messages the operator has authorized. Radiated is false: the desk does not key a radio. Does not transmit.")]
    public IReadOnlyList<AuthorizedTransmission> list_authorized() => desk.Snapshot().Authorized;

    static T Guard<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (ArgumentException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    static async Task<T> GuardAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or RadioException)
        {
            throw new McpException(ex.Message);
        }
    }
}
