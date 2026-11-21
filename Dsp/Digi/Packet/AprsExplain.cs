using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace AgenticSdr.Dsp;

/// <summary>
/// Turns terse APRS / TNC-2 lines into a short plain-English sentence for new operators.
/// Keeps the raw line available so experienced ops can still see the wire format.
/// </summary>
public static partial class AprsExplain
{
    static readonly Dictionary<string, string> DestinationHints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["APRS"] = "a generic APRS destination",
        ["APN382"] = "a Kantronics KPC-3 digipeater/TNC",
        ["APN391"] = "a Kantronics KPC-3+",
        ["APDW14"] = "Dire Wolf",
        ["APDW15"] = "Dire Wolf",
        ["APDW16"] = "Dire Wolf",
        ["APDW17"] = "Dire Wolf",
        ["APDW18"] = "Dire Wolf",
        ["APDW19"] = "Dire Wolf",
        ["APDW20"] = "Dire Wolf",
        ["AP1WWX"] = "a weather station",
        ["APX"] = "Xastir",
        ["APY"] = "YaAC",
        ["APK"] = "Kenwood radio APRS",
        ["APZ"] = "experimental / custom software"
    };

    /// <summary>Plain English only. Empty when the line cannot be explained.</summary>
    public static string Describe(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return "";

        string text = Normalize(line);
        if (!TrySplit(text, out string source, out string dest, out List<PathHop> path, out string payload))
            return "";

        var bits = new List<string>();
        bits.Add($"{source} sent an APRS packet");

        string destHint = DescribeDestination(dest);
        if (!string.IsNullOrEmpty(destHint))
            bits.Add($"using {destHint}");

        string via = DescribePath(path);
        if (!string.IsNullOrEmpty(via))
            bits.Add(via);

        string body = DescribePayload(source, payload);
        string head = string.Join(" ", bits) + ".";
        if (string.IsNullOrEmpty(body))
            return head;
        return head + " " + body;
    }

    /// <summary>English summary plus the original TNC-2 line for reference.</summary>
    public static string Format(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return "";
        string raw = Normalize(line);
        // Already explained (e.g. Decode then AppendPacket).
        if (raw.Contains("\nRaw:", StringComparison.Ordinal) || raw.StartsWith("Raw:", StringComparison.Ordinal))
            return raw;
        string english = Describe(raw);
        if (string.IsNullOrWhiteSpace(english))
            return raw;
        return english + "\nRaw: " + raw;
    }

    /// <summary>Formats each decoded packet line; blank lines pass through.</summary>
    public static string FormatDecoded(string? decoded)
    {
        if (string.IsNullOrWhiteSpace(decoded))
            return "";
        var parts = new List<string>();
        foreach (string line in decoded.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            parts.Add(Format(line.Trim()));
        }

        return string.Join("\n\n", parts);
    }

    static string Normalize(string line)
    {
        string text = WebUtility.HtmlDecode(line).Trim();
        text = text.Replace('\u00a0', ' ');
        return text;
    }

    static bool TrySplit(string line, out string source, out string dest, out List<PathHop> path, out string payload)
    {
        source = "";
        dest = "";
        path = [];
        payload = "";
        int colon = line.IndexOf(':');
        if (colon <= 0 || colon >= line.Length - 1)
            return false;
        string header = line[..colon];
        payload = line[(colon + 1)..];
        int gt = header.IndexOf('>');
        if (gt <= 0)
            return false;
        source = header[..gt].Trim();
        string rest = header[(gt + 1)..].Trim();
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(rest))
            return false;

        string[] hops = rest.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (hops.Length == 0)
            return false;
        dest = hops[0];
        for (int i = 1; i < hops.Length; i++)
        {
            string hop = hops[i];
            bool used = hop.EndsWith('*');
            if (used)
                hop = hop[..^1];
            path.Add(new PathHop(hop, used));
        }

        return true;
    }

    static string DescribeDestination(string dest)
    {
        string call = dest.Split('-', 2)[0];
        if (DestinationHints.TryGetValue(dest, out string? exact))
            return exact;
        if (DestinationHints.TryGetValue(call, out string? byCall))
            return byCall;
        foreach ((string key, string value) in DestinationHints)
        {
            if (key.Length >= 3 && dest.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                return value;
        }

        if (dest.StartsWith("AP", StringComparison.OrdinalIgnoreCase))
            return $"APRS software identified as {dest}";
        return $"destination {dest}";
    }

    static string DescribePath(List<PathHop> path)
    {
        if (path.Count == 0)
            return "with no digipeater path (direct)";

        var used = path.Where(h => h.Used).Select(h => h.Name).ToList();
        var pending = path.Where(h => !h.Used).Select(h => DescribeHopRole(h.Name)).ToList();
        var parts = new List<string>();
        if (used.Count > 0)
            parts.Add("already relayed by " + JoinList(used));
        if (pending.Count > 0)
            parts.Add("asking to go via " + JoinList(pending));
        return string.Join(", ", parts);
    }

    static string DescribeHopRole(string name)
    {
        if (WideHop().IsMatch(name))
            return $"{name} (wide-area relay request)";
        if (name.Equals("GATE", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("TCP", StringComparison.OrdinalIgnoreCase)
            || name.Contains("IGATE", StringComparison.OrdinalIgnoreCase))
            return $"{name} (internet gateway)";
        return name;
    }

    static string DescribePayload(string source, string payload)
    {
        if (string.IsNullOrEmpty(payload))
            return "No information field.";

        if (TryDescribeUltimeter(payload, out string ultimeter))
            return ultimeter;

        if (TryDescribeTelemetryT(payload, out string telemetry))
            return telemetry;

        if (TryDescribeTelemetryDefinition(payload, out string definition))
            return definition;

        if (payload.StartsWith(":", StringComparison.Ordinal) && payload.Length > 1)
            return "Message text: " + TrimComment(payload[1..]);

        if (payload.StartsWith(">", StringComparison.Ordinal))
            return "Status text: " + TrimComment(payload[1..]);

        if (TryParsePosition(payload, out PositionInfo pos))
        {
            var sb = new StringBuilder();
            sb.Append(DescribeStationRole(source, pos));
            sb.Append(" at ");
            sb.Append(FormatLatLon(pos.LatDegrees, pos.LonDegrees));
            sb.Append('.');
            if (pos.Weather is not null)
            {
                sb.Append(' ');
                sb.Append(pos.Weather);
            }

            if (pos.Phg is not null)
            {
                sb.Append(' ');
                sb.Append(pos.Phg);
            }

            if (!string.IsNullOrWhiteSpace(pos.Comment))
            {
                sb.Append(' ');
                sb.Append("Comment: ");
                sb.Append(pos.Comment);
            }

            return sb.ToString();
        }

        if (payload.Length > 0 && payload[0] >= 32 && payload[0] <= 126)
            return "Information field: " + TrimComment(payload);
        return "Binary or unrecognized APRS payload.";
    }

    /// <summary>
    /// Peet Bros Ultimeter logging mode (<c>!!</c>) and packet mode (<c>$ULTW</c>).
    /// Fields are 4 hex digits; <c>----</c> means that sensor is absent.
    /// </summary>
    public static bool TryDescribeUltimeter(string payload, out string text)
    {
        text = "";
        if (payload.StartsWith("!!", StringComparison.Ordinal))
            return TryDescribeUltimeterLogger(payload[2..], out text);
        if (payload.StartsWith("$ULTW", StringComparison.OrdinalIgnoreCase))
            return TryDescribeUltimeterPacket(payload["$ULTW".Length..], out text);
        return false;
    }

    static bool TryDescribeUltimeterLogger(string hex, out string text)
    {
        text = "";
        // 1 wind 0.1 kph, 2 dir 0-255, 3 outdoor °F×10, 4 rain 0.01", 5 baro 0.1 mbar,
        // 6 indoor °F×10, 7 outdoor humidity 0.1%, 8 indoor humidity 0.1%,
        // 9 day-of-year, 10 minute-of-day, 11 today's rain 0.01", 12 1-min wind avg 0.1 kph
        if (!TryReadHexFields(hex, 12, out short?[] f) || f[0] is null && f[1] is null && f[2] is null)
            return false;

        var parts = new List<string>();
        short? windField = f[0] ?? f[11];
        if (windField is short windRaw)
        {
            double mph = windRaw * 0.1 * 0.621371;
            if (f[1] is short dirRaw)
            {
                double deg = (dirRaw & 0xff) * 360.0 / 256.0;
                parts.Add(mph < 0.3
                    ? $"wind calm (from about {deg:0}°)"
                    : $"wind from {deg:0}° at {mph:0.0} mph");
            }
            else if (mph >= 0.3)
                parts.Add($"wind {mph:0.0} mph");
        }

        if (f[2] is short ot)
            parts.Add($"outdoor temperature {ot * 0.1:0.0} °F");
        if (f[5] is short it)
            parts.Add($"indoor temperature {it * 0.1:0.0} °F");
        if (f[4] is short baro)
            parts.Add($"barometer {baro * 0.1:0.0} mbar");
        if (f[6] is short oh)
            parts.Add($"outdoor humidity {oh * 0.1:0.#}%");
        if (f[7] is short ih)
            parts.Add($"indoor humidity {ih * 0.1:0.#}%");
        if (f[3] is short rain && rain > 0)
            parts.Add($"long-term rain {rain * 0.01:0.##} in");
        if (f[10] is short today && today > 0)
            parts.Add($"rain today {today * 0.01:0.##} in");
        if (f[8] is short day && f[9] is short minute)
        {
            int hh = minute / 60;
            int mm = minute % 60;
            parts.Add($"station clock day {day} of year, {hh:00}:{mm:00}");
        }

        if (parts.Count == 0)
            return false;
        text = "Ultimeter weather telemetry: " + JoinList(parts) + ".";
        return true;
    }

    static bool TryDescribeUltimeterPacket(string hex, out string text)
    {
        text = "";
        // Packet mode field order differs after field 5 (baro correction instead of indoor sensors).
        if (!TryReadHexFields(hex, 13, out short?[] f) || f[0] is null && f[2] is null)
            return false;

        var parts = new List<string>();
        if (f[0] is short peak)
        {
            double mph = peak * 0.1 * 0.621371;
            if (f[1] is short dirRaw)
            {
                double deg = (dirRaw & 0xff) * 360.0 / 256.0;
                parts.Add(mph < 0.3
                    ? $"peak wind calm (from about {deg:0}°)"
                    : $"peak wind from {deg:0}° at {mph:0.0} mph");
            }
            else if (mph >= 0.3)
                parts.Add($"peak wind {mph:0.0} mph");
        }

        if (f[2] is short ot)
            parts.Add($"outdoor temperature {ot * 0.1:0.0} °F");
        if (f[4] is short baro)
            parts.Add($"barometer {baro * 0.1:0.0} mbar");
        if (f[8] is short oh)
            parts.Add($"outdoor humidity {oh * 0.1:0.#}%");
        if (f[3] is short rain && rain > 0)
            parts.Add($"long-term rain {rain * 0.01:0.##} in");
        if (f[11] is short today && today > 0)
            parts.Add($"rain today {today * 0.01:0.##} in");
        if (f[12] is short avg)
        {
            double mph = avg * 0.1 * 0.621371;
            if (mph >= 0.3)
                parts.Add($"5-minute average wind {mph:0.0} mph");
        }

        if (parts.Count == 0)
            return false;
        text = "Ultimeter weather telemetry (packet mode): " + JoinList(parts) + ".";
        return true;
    }

    /// <summary>Standard APRS <c>T#seq,a1,a2,a3,a4,a5,digital</c> telemetry data report.</summary>
    public static bool TryDescribeTelemetryT(string payload, out string text)
    {
        text = "";
        var m = TelemetryData().Match(payload);
        if (!m.Success)
            return false;

        string seq = m.Groups["seq"].Value;
        var analogs = new List<string>();
        for (int i = 1; i <= 5; i++)
        {
            if (!m.Groups[$"a{i}"].Success)
                continue;
            string raw = m.Groups[$"a{i}"].Value;
            if (raw is "." or "-" or "")
                continue;
            analogs.Add($"channel {i} = {raw}");
        }

        string bits = m.Groups["bits"].Success ? m.Groups["bits"].Value : "";
        var parts = new List<string> { $"sequence {seq}" };
        if (analogs.Count > 0)
            parts.Add(JoinList(analogs));
        if (!string.IsNullOrEmpty(bits) && bits.Any(c => c == '1'))
        {
            var on = new List<string>();
            for (int i = 0; i < bits.Length && i < 8; i++)
            {
                if (bits[i] == '1')
                    on.Add($"bit {i + 1}");
            }

            if (on.Count > 0)
                parts.Add("digital inputs on for " + JoinList(on));
        }
        else if (!string.IsNullOrEmpty(bits))
            parts.Add("all digital inputs off");

        text = "APRS telemetry data report: " + JoinList(parts) + ".";
        return true;
    }

    /// <summary>PARM / UNIT / EQNS / BITS definition messages that label later T# reports.</summary>
    public static bool TryDescribeTelemetryDefinition(string payload, out string text)
    {
        text = "";
        if (!payload.StartsWith(':') || payload.Length < 12)
            return false;

        // :ADDRESSE:PARM.Name1,Name2,...  (addressee is 9 chars)
        string rest = payload[1..];
        if (rest.Length < 10 || rest[9] != ':')
            return false;
        string addressee = rest[..9].Trim();
        string body = rest[10..];
        string kind;
        string detail;
        if (body.StartsWith("PARM.", StringComparison.OrdinalIgnoreCase))
        {
            kind = "parameter names";
            detail = body["PARM.".Length..];
        }
        else if (body.StartsWith("UNIT.", StringComparison.OrdinalIgnoreCase))
        {
            kind = "units";
            detail = body["UNIT.".Length..];
        }
        else if (body.StartsWith("EQNS.", StringComparison.OrdinalIgnoreCase))
        {
            kind = "scaling equations (a·x² + b·x + c per channel)";
            detail = body["EQNS.".Length..];
        }
        else if (body.StartsWith("BITS.", StringComparison.OrdinalIgnoreCase))
        {
            kind = "digital bit labels";
            detail = body["BITS.".Length..];
        }
        else
            return false;

        text = string.IsNullOrEmpty(addressee)
            ? $"Telemetry setup message defining {kind}: {TrimComment(detail)}."
            : $"Telemetry setup for {addressee} defining {kind}: {TrimComment(detail)}.";
        return true;
    }

    static bool TryReadHexFields(string hex, int maxFields, out short?[] fields)
    {
        fields = new short?[maxFields];
        string block = hex.Trim();
        int end = 0;
        while (end < block.Length && (Uri.IsHexDigit(block[end]) || block[end] == '-'))
            end++;
        block = block[..end];
        if (block.Length < 8)
            return false;

        for (int i = 0; i < maxFields; i++)
        {
            int at = i * 4;
            if (at + 4 > block.Length)
                break;
            string field = block.Substring(at, 4);
            if (field == "----")
            {
                fields[i] = null;
                continue;
            }

            if (field.Contains('-', StringComparison.Ordinal))
                return false;
            if (!short.TryParse(field, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out short value))
                return false;
            fields[i] = value;
        }

        return true;
    }

    static string DescribeStationRole(string source, PositionInfo pos)
    {
        string comment = pos.Comment ?? "";
        if (pos.IsWeather
            || comment.Contains("WX", StringComparison.OrdinalIgnoreCase)
            || comment.Contains("weather", StringComparison.OrdinalIgnoreCase))
            return $"{source} is a weather report";
        if (comment.Contains("Igate", StringComparison.OrdinalIgnoreCase)
            || comment.Contains("IGate", StringComparison.OrdinalIgnoreCase)
            || comment.Contains("IGATE", StringComparison.OrdinalIgnoreCase))
            return $"{source} is an internet gateway (IGate) / digipeater";
        if (comment.Contains("Digi", StringComparison.OrdinalIgnoreCase)
            || pos.Symbol is '#' or 'r' or 'n')
            return $"{source} is a digipeater (packet relay)";
        if (pos.Symbol is '_' )
            return $"{source} is a weather station";
        if (pos.Symbol is '-' or 'k' or '>')
            return $"{source} is a house / station beacon";
        return $"{source} reports a position";
    }

    static bool TryParsePosition(string payload, out PositionInfo info)
    {
        info = default!;
        string body = payload;
        string? timestamp = null;
        if (body.Length > 1 && body[0] is '@' or '/')
        {
            // @DDHHMMz or @DDHHMMh / same with /
            if (body.Length >= 8 && char.IsDigit(body[1]))
            {
                timestamp = body[1..8];
                body = body[8..];
            }
            else
                body = body[1..];
        }
        else if (body.Length > 0 && body[0] is '!' or '=' or '\'' or '`')
        {
            body = body[1..];
        }
        else
            return false;

        // Uncompressed: DDMM.mmN x DDDMM.mmW s
        var m = UncompressedPosition().Match(body);
        if (!m.Success)
            return false;

        if (!TryParseLat(m.Groups["lat"].Value, m.Groups["ns"].Value, out double lat))
            return false;
        if (!TryParseLon(m.Groups["lon"].Value, m.Groups["ew"].Value, out double lon))
            return false;

        char table = m.Groups["table"].Value[0];
        char symbol = m.Groups["symbol"].Value[0];
        string rest = m.Groups["rest"].Value;

        string? weather = null;
        string? phg = null;
        string comment = rest;

        if (symbol == '_' || table == '/')
        {
            // Weather data often follows immediately after the symbol for '_' stations.
            if (TryTakeWeather(ref comment, out string wx))
                weather = wx;
        }

        // Course/speed before PHG on some beacons: ddd/sss
        comment = StripLeadingCourseSpeed(comment);

        if (TryTakePhg(ref comment, out string phgText))
            phg = phgText;

        comment = TrimComment(comment);
        info = new PositionInfo(lat, lon, table, symbol, symbol == '_', weather, phg, comment, timestamp);
        return true;
    }

    static bool TryParseLat(string ddmm, string ns, out double degrees)
    {
        degrees = 0;
        if (ddmm.Length < 7 || !double.TryParse(ddmm, NumberStyles.Float, CultureInfo.InvariantCulture, out double raw))
            return false;
        int deg = (int)(raw / 100);
        double minutes = raw - deg * 100;
        degrees = deg + minutes / 60.0;
        if (ns is "S" or "s")
            degrees = -degrees;
        return true;
    }

    static bool TryParseLon(string dddmm, string ew, out double degrees)
    {
        degrees = 0;
        if (dddmm.Length < 8 || !double.TryParse(dddmm, NumberStyles.Float, CultureInfo.InvariantCulture, out double raw))
            return false;
        int deg = (int)(raw / 100);
        double minutes = raw - deg * 100;
        degrees = deg + minutes / 60.0;
        if (ew is "W" or "w")
            degrees = -degrees;
        return true;
    }

    static string FormatLatLon(double lat, double lon)
    {
        string ns = lat >= 0 ? "N" : "S";
        string ew = lon >= 0 ? "E" : "W";
        return string.Create(CultureInfo.InvariantCulture,
            $"{Math.Abs(lat):0.####}°{ns}, {Math.Abs(lon):0.####}°{ew}");
    }

    static bool TryTakePhg(ref string comment, out string text)
    {
        text = "";
        var m = PhgCode().Match(comment);
        if (!m.Success)
            return false;
        string code = m.Groups[1].Value;
        comment = (comment[..m.Index] + comment[(m.Index + m.Length)..]).TrimStart('/', ' ');
        if (code.Length < 4)
            return false;

        int p = code[0] - '0';
        int h = code[1] - '0';
        int g = code[2] - '0';
        int d = code[3] - '0';
        if ((uint)p > 9 || (uint)h > 9 || (uint)g > 9 || (uint)d > 9)
            return false;

        int watts = p * p;
        int heightFt = 10 * (1 << h);
        string dir = d == 0 ? "omnidirectional" : $"{d * 45}° from north";
        text = $"Radio about {watts} W, antenna roughly {heightFt} ft above average terrain, {g} dB gain, {dir}.";
        return true;
    }

    static bool TryTakeWeather(ref string comment, out string text)
    {
        text = "";
        // Common: ccc/sss or course/speed then g…t… etc.
        var m = WeatherBlock().Match(comment);
        if (!m.Success)
            return false;

        comment = comment[m.Length..].TrimStart();
        var parts = new List<string>();
        if (m.Groups["course"].Success && m.Groups["speed"].Success)
        {
            int course = int.Parse(m.Groups["course"].Value, CultureInfo.InvariantCulture);
            int speed = int.Parse(m.Groups["speed"].Value, CultureInfo.InvariantCulture);
            if (speed == 0 && course == 0)
                parts.Add("wind calm");
            else if (speed == 0)
                parts.Add($"wind from {course}° (calm)");
            else
                parts.Add($"wind from {course}° at {speed} mph");
        }

        if (m.Groups["gust"].Success)
            parts.Add($"gust {m.Groups["gust"].Value} mph");
        if (m.Groups["temp"].Success
            && int.TryParse(m.Groups["temp"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ti))
            parts.Add($"temperature {ti} °F");

        if (m.Groups["rain1"].Success && m.Groups["rain1"].Value != "000")
            parts.Add($"rain last hour {int.Parse(m.Groups["rain1"].Value, CultureInfo.InvariantCulture) / 100.0:0.##} in");
        if (m.Groups["hum"].Success)
        {
            int h = int.Parse(m.Groups["hum"].Value, CultureInfo.InvariantCulture);
            if (h == 0)
                h = 100;
            parts.Add($"humidity {h}%");
        }

        if (m.Groups["baro"].Success
            && int.TryParse(m.Groups["baro"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int baro))
            parts.Add($"pressure {baro / 10.0:0.0} mbar");

        if (parts.Count == 0)
            return false;
        text = "Weather: " + JoinList(parts) + ".";
        return true;
    }

    static string StripLeadingCourseSpeed(string comment)
    {
        var m = CourseSpeed().Match(comment);
        if (!m.Success)
            return comment;
        return comment[m.Length..].TrimStart();
    }

    static string TrimComment(string text)
    {
        string t = text.Trim().TrimStart('/', ' ');
        while (t.Contains("  ", StringComparison.Ordinal))
            t = t.Replace("  ", " ", StringComparison.Ordinal);
        return t.Trim();
    }

    static string JoinList(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        2 => items[0] + " and " + items[1],
        _ => string.Join(", ", items.Take(items.Count - 1)) + ", and " + items[^1]
    };

    [GeneratedRegex(@"^WIDE\d(?:-\d)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WideHop();

    [GeneratedRegex(
        @"^(?<lat>\d{4}\.\d{2})(?<ns>[NnSs])(?<table>.)(?<lon>\d{5}\.\d{2})(?<ew>[EeWw])(?<symbol>.)(?<rest>.*)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex UncompressedPosition();

    [GeneratedRegex(@"PHG(\d{4,5})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PhgCode();

    [GeneratedRegex(@"^(?<course>\d{3})/(?<speed>\d{3})(?:g(?<gust>\d{3}))?(?:t(?<temp>-?\d{2,3}))?(?:r(?<rain1>\d{3}))?(?:p(?<rain24>\d{3}))?(?:P(?<rainMid>\d{3}))?(?:h(?<hum>\d{2}))?(?:b(?<baro>\d{4,5}))?",
        RegexOptions.CultureInvariant)]
    private static partial Regex WeatherBlock();

    [GeneratedRegex(@"^\d{3}/\d{3}")]
    private static partial Regex CourseSpeed();

    [GeneratedRegex(
        @"^T#(?<seq>\d{1,3}),(?<a1>[0-9.\-]+),(?<a2>[0-9.\-]+),(?<a3>[0-9.\-]+),(?<a4>[0-9.\-]+),(?<a5>[0-9.\-]+)(?:,(?<bits>[01]{8}))?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TelemetryData();

    sealed record PathHop(string Name, bool Used);

    sealed record PositionInfo(
        double LatDegrees,
        double LonDegrees,
        char Table,
        char Symbol,
        bool IsWeather,
        string? Weather,
        string? Phg,
        string Comment,
        string? Timestamp);
}
