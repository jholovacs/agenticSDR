using System.Globalization;
using System.Text.RegularExpressions;

namespace AgenticSdr.Dsp;

/// <summary>Call + Maidenhead fix extracted from a digimode decode line (FT8, JS8, WSPR, free text).</summary>
public readonly record struct DigiLocationFix(
    string Callsign,
    string Grid,
    double LatitudeDegrees,
    double LongitudeDegrees,
    string Source);

/// <summary>Pull station positions out of digimode message text for the live RF map.</summary>
public static partial class DigiLocation
{
    // Optional wall-clock stamp from the Host transcript, then SNR / Hz / optional JS8 speed.
    [GeneratedRegex(
        @"^(?:(?:\d{2}:\d{2}:\d{2})\s+)?(?:[+\-]?\d+(?:\.\d+)?\s*dB\s+)?(?:\d+(?:\.\d+)?\s*Hz(?:\s+\([^)]+\))?\s+)?",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex PrefixRegex();

    // Maidenhead field (2 letters A–R) + square (2 digits) + optional subsquare (2 letters A–X).
    [GeneratedRegex(
        @"\b([A-R]{2}\d{2}(?:[A-X]{2})?)\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex GridRegex();

    /// <summary>
    /// Parse a Format() message line (or transcript line) for a callsign that reported a Maidenhead grid.
    /// Four- and six-character squares plot at the square center — no separate lat/lon needed.
    /// </summary>
    public static bool TryParse(string? line, string mode, out DigiLocationFix fix)
    {
        fix = default;
        if (string.IsNullOrWhiteSpace(line))
            return false;

        string source = CanonicalSource(mode);
        string body = PrefixRegex().Replace(line.Trim(), "", 1).Trim();
        if (body.Length == 0)
            return false;

        // JS8 renders "CALL: …"; strip the colon so call checks and free-text work.
        string[] parts = body
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeToken)
            .Where(p => p.Length > 0)
            .ToArray();
        if (parts.Length == 0)
            return false;

        // WSPR Type-1: CALL GRID dBm
        if (source == "wspr" && TryParseWspr(parts, out fix))
            return true;

        // FT8 / JS8 shapes (CQ … GRID / TO FROM GRID). Try for every mode so a mismatched
        // RadioMode still plots the Maidenhead center under the real decoder source label.
        if (TryParseCqGrid(parts, source, out fix))
            return true;
        if (TryParseExchangeGrid(parts, source, out fix))
            return true;
        if (TryParseJs8Grid(parts, source, out fix))
            return true;

        // Free-text digimodes (Olivia, JTTY, VarAC, JS8 heartbeat, …): first call + any Maidenhead.
        return TryParseFreeText(parts, source, out fix);
    }

    private static string NormalizeToken(string token)
    {
        string t = token.Trim();
        // JS8 "KJ5SKJ:" / directed "W1AW>"
        while (t.Length > 0 && (t[^1] is ':' or '>' or ',' or ';' or '.'))
            t = t[..^1];
        while (t.Length > 0 && (t[0] is '<' or '(' or '['))
            t = t[1..];
        while (t.Length > 0 && (t[^1] is '>' or ')' or ']'))
            t = t[..^1];
        return t;
    }

    private static bool TryParseWspr(string[] parts, out DigiLocationFix fix)
    {
        fix = default;
        // CALL GRID POWER — power is a small integer dBm.
        if (parts.Length < 3)
            return false;
        if (!LooksLikeCall(parts[0]))
            return false;
        if (!IsLocationGrid(parts[1], out double lat, out double lon))
            return false;
        if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            return false;

        fix = new DigiLocationFix(parts[0].ToUpperInvariant(), parts[1].ToUpperInvariant(), lat, lon, "wspr");
        return true;
    }

    private static bool TryParseCqGrid(string[] parts, string source, out DigiLocationFix fix)
    {
        fix = default;
        if (!parts[0].Equals("CQ", StringComparison.OrdinalIgnoreCase))
            return false;

        // CQ CALL GRID | CQ DX CALL GRID | CQ TARGET CALL GRID
        if (parts.Length < 3)
            return false;

        string gridToken = parts[^1];
        if (!IsLocationGrid(gridToken, out double lat, out double lon))
            return false;

        string call = parts[^2];
        if (!LooksLikeCall(call))
            return false;

        fix = new DigiLocationFix(call.ToUpperInvariant(), gridToken.ToUpperInvariant(), lat, lon, source);
        return true;
    }

    private static bool TryParseExchangeGrid(string[] parts, string source, out DigiLocationFix fix)
    {
        fix = default;
        // TO FROM GRID — the second call is reporting its grid (FT8 / JS8 standard).
        if (parts.Length < 3)
            return false;
        if (!LooksLikeCall(parts[0]) || !LooksLikeCall(parts[1]))
            return false;

        // Grid is usually the third token; also accept it as the last token when trailing junk exists.
        string gridToken = parts[2];
        if (!IsLocationGrid(gridToken, out double lat, out double lon)
            && parts.Length > 3
            && IsLocationGrid(parts[^1], out lat, out lon))
        {
            gridToken = parts[^1];
        }
        else if (!IsLocationGrid(gridToken, out lat, out lon))
            return false;

        fix = new DigiLocationFix(parts[1].ToUpperInvariant(), gridToken.ToUpperInvariant(), lat, lon, source);
        return true;
    }

    /// <summary>
    /// JS8 heartbeat / compound forms: "CALL: @HB HEARTBEAT EM13", "CALL: @ALLCALL CQ EM13", "CALL: EM13".
    /// </summary>
    private static bool TryParseJs8Grid(string[] parts, string source, out DigiLocationFix fix)
    {
        fix = default;
        if (parts.Length < 2)
            return false;
        if (!LooksLikeCall(parts[0]))
            return false;

        for (int i = 1; i < parts.Length; i++)
        {
            if (!IsLocationGrid(parts[i], out double lat, out double lon))
                continue;
            // Prefer the last Maidenhead on the line (status text can mention others).
            string grid = parts[i].ToUpperInvariant();
            // Keep scanning so a trailing 6-char square wins over an earlier 4-char.
            fix = new DigiLocationFix(parts[0].ToUpperInvariant(), grid, lat, lon, source);
        }

        return fix.Callsign is not null;
    }

    /// <summary>
    /// Maidenhead square that is not an FT8 report / sign-off token (RR73 looks like a grid).
    /// </summary>
    private static bool IsLocationGrid(string token, out double lat, out double lon)
    {
        lat = 0;
        lon = 0;
        if (token is "RR73" or "RRR" or "73" or "TU")
            return false;
        if (token.Length >= 2 && (token[0] is 'R' or 'r') && (token[1] is '+' or '-'))
            return false;
        return Maidenhead.TryParse(token, out lat, out lon);
    }

    private static bool TryParseFreeText(string[] parts, string source, out DigiLocationFix fix)
    {
        fix = default;
        string? call = null;
        foreach (string part in parts)
        {
            if (!LooksLikeCall(part))
                continue;
            call = part.ToUpperInvariant();
            break;
        }

        if (call is null)
            return false;

        string? grid = null;
        double lat = 0, lon = 0;
        foreach (Match match in GridRegex().Matches(string.Join(' ', parts)))
        {
            string candidate = match.Groups[1].Value.ToUpperInvariant();
            if (!IsLocationGrid(candidate, out double cLat, out double cLon))
                continue;
            // Prefer 6-character when both appear; otherwise take the last valid square.
            if (grid is not null && candidate.Length < grid.Length)
                continue;
            grid = candidate;
            lat = cLat;
            lon = cLon;
        }

        if (grid is null)
            return false;

        fix = new DigiLocationFix(call, grid, lat, lon, source);
        return true;
    }

    private static string CanonicalSource(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
            return "digi";
        string m = mode.Trim().ToLowerInvariant();
        return m switch
        {
            "ft8" => "ft8",
            "js8" => "js8",
            "wspr" => "wspr",
            "olivia" => "olivia",
            "jtty" => "jtty",
            "varac" => "varac",
            "packet" or "aprs" => "aprs",
            _ => m
        };
    }

    /// <summary>Loose amateur call check — digit somewhere after a letter, length 3–10.</summary>
    private static bool LooksLikeCall(string token)
    {
        if (token.Length is < 3 or > 10)
            return false;
        if (token.Equals("CQ", StringComparison.OrdinalIgnoreCase)
            || token.Equals("DX", StringComparison.OrdinalIgnoreCase)
            || token.Equals("QRZ", StringComparison.OrdinalIgnoreCase)
            || token.Equals("DE", StringComparison.OrdinalIgnoreCase)
            || token.StartsWith('@') // JS8 @HB / @ALLCALL
            || token is "RR73" or "RRR" or "73" or "TU" or "HEARTBEAT")
            return false;
        if (token[0] is '+' or '-')
            return false;
        if (Maidenhead.TryParse(token, out _, out _))
            return false;

        bool letter = false;
        bool digit = false;
        foreach (char c in token)
        {
            if (c is '/' or '-')
                continue;
            if (char.IsLetter(c))
                letter = true;
            else if (char.IsDigit(c))
                digit = true;
            else
                return false;
        }

        return letter && digit;
    }
}
