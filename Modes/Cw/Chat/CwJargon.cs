using System.Text;
using System.Text.RegularExpressions;

namespace AgenticSdr.Cw.Chat;

/// <summary>
/// Plain-language help for common CW Q-codes, prosigns, and abbreviations — for RX copy or TX drafts.
/// </summary>
public static partial class CwJargon
{
    static readonly Dictionary<string, string> Terms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CQ"] = "general call — looking for anyone to answer",
        ["DE"] = "from / this is",
        ["K"] = "over — go ahead",
        ["KN"] = "over to you only (specific station)",
        ["BK"] = "break — back to you",
        ["AR"] = "end of message",
        ["AS"] = "wait / stand by",
        ["SK"] = "end of contact / silent key",
        ["CL"] = "closing the station",
        ["BT"] = "break / separator (=)",
        ["HH"] = "correction — error, starting over",
        ["AA"] = "all after",
        ["AB"] = "all before",
        ["BN"] = "all between",
        ["WA"] = "word after",
        ["WB"] = "word before",
        ["QRL"] = "is this frequency in use?",
        ["QRM"] = "man-made interference",
        ["QRN"] = "atmospheric noise / static",
        ["QRO"] = "increase power",
        ["QRP"] = "decrease power / low power",
        ["QRS"] = "send more slowly",
        ["QRQ"] = "send faster",
        ["QRT"] = "stop sending / going off the air",
        ["QRU"] = "have you anything for me?",
        ["QRV"] = "I am ready",
        ["QRX"] = "stand by / I will call you again",
        ["QRZ"] = "who is calling me?",
        ["QSB"] = "fading",
        ["QSL"] = "acknowledge receipt / confirm",
        ["QSO"] = "a contact / conversation",
        ["QSY"] = "change frequency",
        ["QTH"] = "location",
        ["QTR"] = "exact time",
        ["73"] = "best regards",
        ["88"] = "love and kisses",
        ["OM"] = "old man (friendly address for an operator)",
        ["YL"] = "young lady",
        ["XYL"] = "wife",
        ["ES"] = "and",
        ["FB"] = "fine business — good / excellent",
        ["GM"] = "good morning",
        ["GA"] = "good afternoon / go ahead",
        ["GE"] = "good evening",
        ["TU"] = "thank you",
        ["TNX"] = "thanks",
        ["HW"] = "how",
        ["CPY"] = "copy (how well you are receiving)",
        ["RST"] = "readability, strength, tone report",
        ["UR"] = "your",
        ["MY"] = "my",
        ["NR"] = "number",
        ["PSE"] = "please",
        ["AGN"] = "again",
        ["R"] = "roger — received",
        ["RR"] = "roger roger — solid copy",
        ["NIL"] = "nothing / no traffic",
        ["WX"] = "weather",
        ["TEMP"] = "temperature",
        ["NAME"] = "name",
        ["RIG"] = "radio equipment",
        ["ANT"] = "antenna",
        ["PWR"] = "power",
        ["WPM"] = "words per minute",
        ["UP"] = "listen up in frequency",
        ["DWN"] = "listen down in frequency",
        ["DX"] = "long-distance contact",
        ["TEST"] = "test transmission",
    };

    public sealed record CwTerm(string Term, string Meaning);

    public sealed record CwExplanation(string Direction, string Text, string Summary, IReadOnlyList<CwTerm> Terms);

    /// <summary>
    /// Explain CW text for an operator. Direction is <c>rx</c> (heard) or <c>tx</c> (about to send).
    /// </summary>
    public static CwExplanation Explain(string? text, string? direction)
    {
        string raw = (text ?? "").Trim().ToUpperInvariant();
        string dir = string.Equals(direction, "tx", StringComparison.OrdinalIgnoreCase) ? "tx" : "rx";
        if (raw.Length == 0)
        {
            return new CwExplanation(
                dir,
                "",
                dir == "tx" ? "Nothing to draft yet." : "Nothing heard to translate.",
                []);
        }

        var found = new List<CwTerm>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in TokenRegex().Matches(raw))
        {
            string token = match.Value;
            if (!Terms.TryGetValue(token, out string? meaning) || !seen.Add(token))
                continue;
            found.Add(new CwTerm(token, meaning));
        }

        var prose = new StringBuilder();
        if (dir == "tx")
            prose.Append("Outbound CW draft: ");
        else
            prose.Append("Received CW: ");
        prose.Append('"').Append(raw).Append("\". ");
        if (found.Count == 0)
            prose.Append("No standard Q-codes or prosigns matched; read it as call signs and plain text.");
        else
        {
            prose.Append("Key jargon — ");
            for (int i = 0; i < found.Count; i++)
            {
                if (i > 0) prose.Append("; ");
                prose.Append(found[i].Term).Append(" = ").Append(found[i].Meaning);
            }
            prose.Append('.');
        }

        return new CwExplanation(dir, raw, prose.ToString(), found);
    }

    [GeneratedRegex(@"[A-Z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();
}
