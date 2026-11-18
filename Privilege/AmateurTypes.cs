using System.Text.RegularExpressions;

namespace AgenticSdr.Privilege;

/// <summary>FCC operator class currently issued for US amateur stations: Technician, General, or Amateur Extra.</summary>
public enum LicenseClass
{
    Technician,
    General,
    Extra
}

/// <summary>
/// How the station would put information on the air. Phone is voice. Data is a keyboard or weak-signal digital mode.
/// CW is Morse. The occupancy of each mode is what the privilege check keeps inside the authorized segment.
/// </summary>
public enum AmateurMode
{
    Cw,
    Usb,
    Lsb,
    Am,
    Nbfm,
    Data,
    Ft8,
    Js8,
    Olivia,
    Jtty,
    Wspr,
    Varac,
    Packet,
    Ribbit
}

/// <summary>
/// Result of comparing one frequency, mode, and power with Part 97.
/// Allowed is true only when that comparison passed. Transmit still requires an operator click and a TX-capable radio.
/// </summary>
public sealed record PrivilegeDecision(
    bool Allowed,
    bool WithinLicense,
    string Reason,
    string? Band,
    double? MaxWatts,
    string? Caution);

/// <summary>
/// The control operator signed in on a mode desk. This is an attestation, not an FCC database lookup.
/// </summary>
public sealed record OperatorIdentity(
    string CallSign,
    string BaseCall,
    LicenseClass License,
    string? Grid = null,
    double? Latitude = null,
    double? Longitude = null,
    string? Place = null,
    string? Coordinates = null);

/// <summary>
/// Proof that the transmit button was used by the operator.
/// Agents cannot construct this. <see cref="TryFromButton"/> returns one only when the page posts authorized=true.
/// </summary>
public sealed class OperatorClick
{
    private OperatorClick(string requestId) => RequestId = requestId;

    public string RequestId { get; }

    public static bool TryFromButton(string? requestId, bool authorized, out OperatorClick click)
    {
        if (!authorized || string.IsNullOrWhiteSpace(requestId))
        {
            click = null!;
            return false;
        }

        click = new OperatorClick(requestId.Trim());
        return true;
    }
}

/// <summary>Parses license class names and amateur modes from the page and from an agent.</summary>
public static class AmateurWords
{
    public static bool TryParseLicense(string? text, out LicenseClass license)
    {
        license = default;
        string key = Compact(text);
        switch (key)
        {
            case "technician":
            case "tech":
            case "t":
                license = LicenseClass.Technician;
                return true;
            case "general":
            case "gen":
            case "g":
                license = LicenseClass.General;
                return true;
            case "extra":
            case "amateurextra":
            case "e":
            case "ae":
                license = LicenseClass.Extra;
                return true;
            default:
                return false;
        }
    }

    public static bool TryParseMode(string? text, out AmateurMode mode)
    {
        mode = default;
        switch (Compact(text))
        {
            case "cw":
            case "morse":
                mode = AmateurMode.Cw;
                return true;
            case "usb":
                mode = AmateurMode.Usb;
                return true;
            case "lsb":
                mode = AmateurMode.Lsb;
                return true;
            case "am":
                mode = AmateurMode.Am;
                return true;
            case "nbfm":
            case "fm":
                mode = AmateurMode.Nbfm;
                return true;
            case "data":
            case "rtty":
            case "digi":
                mode = AmateurMode.Data;
                return true;
            case "ft8":
                mode = AmateurMode.Ft8;
                return true;
            case "js8":
            case "js8call":
                mode = AmateurMode.Js8;
                return true;
            case "olivia":
                mode = AmateurMode.Olivia;
                return true;
            case "jtty":
                mode = AmateurMode.Jtty;
                return true;
            case "wspr":
            case "whisper":
                mode = AmateurMode.Wspr;
                return true;
            case "varac":
            case "vara":
                mode = AmateurMode.Varac;
                return true;
            case "packet":
            case "aprs":
            case "ax25":
                mode = AmateurMode.Packet;
                return true;
            case "ribbit":
            case "rattlegram":
            case "ori":
                mode = AmateurMode.Ribbit;
                return true;
            default:
                return false;
        }
    }

    public static string LicenseName(LicenseClass license) => license switch
    {
        LicenseClass.Extra => "Amateur Extra",
        _ => license.ToString()
    };

    public static string ModeName(AmateurMode mode) => mode switch
    {
        AmateurMode.Cw => "CW",
        AmateurMode.Usb => "USB phone",
        AmateurMode.Lsb => "LSB phone",
        AmateurMode.Am => "AM phone",
        AmateurMode.Nbfm => "FM phone",
        AmateurMode.Data => "data",
        AmateurMode.Ft8 => "FT8",
        AmateurMode.Js8 => "JS8",
        AmateurMode.Olivia => "Olivia",
        AmateurMode.Jtty => "JTTY",
        AmateurMode.Wspr => "WSPR",
        AmateurMode.Varac => "VarAC",
        AmateurMode.Packet => "packet",
        AmateurMode.Ribbit => "Ribbit",
        _ => mode.ToString()
    };

    static string Compact(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? ""
            : new string(text.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}

/// <summary>US amateur call signs, including a portable indicator such as W1AW/7 or KH6/W1AW.</summary>
public static partial class CallSign
{
    public static bool TryNormalize(string? text, out string display, out string baseCall)
    {
        display = "";
        baseCall = "";
        if (string.IsNullOrWhiteSpace(text))
            return false;

        string candidate = text.Trim().ToUpperInvariant().Replace(" ", "");
        if (!Whole().IsMatch(candidate))
            return false;

        Match found = Base().Match(candidate);
        if (!found.Success)
            return false;

        display = candidate;
        baseCall = found.Value;
        return true;
    }

    /// <summary>True when the message contains the base call sign as its own token, so the station identifies.</summary>
    public static bool IsIdentified(string? message, string baseCall)
    {
        if (string.IsNullOrWhiteSpace(message) || string.IsNullOrWhiteSpace(baseCall))
            return false;

        return Regex.IsMatch(
            message.ToUpperInvariant(),
            $@"(?<![A-Z0-9]){Regex.Escape(baseCall.ToUpperInvariant())}(?![A-Z0-9])");
    }

    [GeneratedRegex(@"^(?:[A-Z]{1,2}\d/)?[AKNW][A-Z]?\d[A-Z]{1,3}(?:/(?:[A-Z]{1,2}\d|\d|[A-Z]{1,3}))?$", RegexOptions.CultureInvariant)]
    private static partial Regex Whole();

    [GeneratedRegex(@"[AKNW][A-Z]?\d[A-Z]{1,3}", RegexOptions.CultureInvariant)]
    private static partial Regex Base();
}
