using System.Text;

namespace AgenticSdr.Dsp;

/// <summary>
/// JS8 message layer: what the 72 payload bits of one frame mean. A frame is one of heartbeat/CQ, compound-callsign,
/// directed (from, to, command), or free text squeezed with a Huffman code. Free text built from the big JSC word list
/// shows its common words and marks the rest as dictionary numbers, because that 262 144-word list is not bundled.
/// </summary>
public static class Js8Text
{
    /// <summary>Frame flag: first frame of a message.</summary>
    public const int TxFirst = 1;
    /// <summary>Frame flag: last frame of a message.</summary>
    public const int TxLast = 2;

    private const string Alphanumeric = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ /@";
    private const uint BaseCall = 37u * 36 * 10 * 27 * 27 * 27;
    private const int BaseGrid = 180 * 180;
    private const int UserGrid = 32410;
    private const int MaxGrid = 32767;
    private const int AltFlag = 0x8000;
    private const int GroupBits = 54872; // 38^3, three callsign characters
    private const int JscSplit = 7;
    private const int JscContinue = 9;

    private static readonly string[] BaseCalls =
    [
        "", "<....>", "@ALLCALL", "@JS8NET", "@DX/NA", "@DX/SA", "@DX/EU", "@DX/AS", "@DX/AF", "@DX/OC", "@DX/AN",
        "@REGION/1", "@REGION/2", "@REGION/3", "@GROUP/0", "@GROUP/1", "@GROUP/2", "@GROUP/3", "@GROUP/4", "@GROUP/5",
        "@GROUP/6", "@GROUP/7", "@GROUP/8", "@GROUP/9", "@COMMAND", "@CONTROL", "@NET", "@NTS", "@RESERVE/0",
        "@RESERVE/1", "@RESERVE/2", "@RESERVE/3", "@RESERVE/4", "@APRSIS", "@RAGCHEW", "@JS8", "@EMCOMM", "@ARES",
        "@MARS", "@AMRRON", "@RACES", "@RAYNET", "@RADAR", "@SKYWARN", "@CQ", "@HB", "@QSO", "@QSOPARTY", "@CONTEST",
        "@FIELDDAY", "@SOTA", "@IOTA", "@POTA", "@QRP", "@QRO"
    ];

    private static readonly string[] CqStrings =
        ["CQ CQ CQ", "CQ DX", "CQ QRP", "CQ CONTEST", "CQ FIELD", "CQ FD", "CQ CQ", "CQ"];

    /// <summary>Directed command names by 5-bit id. Id 31 is the blank command.</summary>
    private static readonly string[] Commands =
    [
        "SNR?", "DIT DIT", "NACK", "HEARING?", "GRID?", ">", "STATUS?", "STATUS", "HEARING", "MSG", "MSG TO:",
        "QUERY", "QUERY MSGS", "QUERY CALL", "ACK", "GRID", "INFO?", "INFO", "FB", "HW CPY?", "SK", "RR", "QSL?",
        "QSL", "CMD", "SNR", "NO", "YES", "73", "HEARTBEAT SNR", "AGN?", ""
    ];

    private static readonly (string Name, int Id)[] CommandAliases =
        [.. Commands.Select((name, id) => (name, id)).Where(item => item.name.Length > 0),
            ("?", 0), ("QUERY MSGS?", 12)];

    private static readonly (string Code, char Letter)[] Huffman =
    [
        ("01", ' '), ("100", 'E'), ("1101", 'T'), ("0011", 'A'), ("11111", 'O'), ("11100", 'I'), ("10111", 'N'),
        ("10100", 'S'), ("00011", 'H'), ("00000", 'R'), ("111011", 'D'), ("110011", 'L'), ("110001", 'C'),
        ("101101", 'U'), ("101011", 'M'), ("001011", 'W'), ("001001", 'F'), ("000101", 'G'), ("000011", 'Y'),
        ("1111011", 'P'), ("1111001", 'B'), ("1110100", '.'), ("1100101", 'V'), ("1100100", 'K'), ("1100001", '-'),
        ("1100000", '+'), ("1011001", '?'), ("1011000", '!'), ("1010101", '"'), ("1010100", 'X'), ("0010101", '0'),
        ("0010100", 'J'), ("0010001", '1'), ("0010000", 'Q'), ("0001001", '2'), ("0001000", 'Z'), ("0000101", '3'),
        ("0000100", '5'), ("11110101", '4'), ("11110100", '9'), ("11110001", '8'), ("11110000", '6'),
        ("11101011", '7'), ("11101010", '/')
    ];

    private static readonly Dictionary<string, char> HuffmanDecode = Huffman.ToDictionary(item => item.Code, item => item.Letter);
    private static readonly Dictionary<char, string> HuffmanEncode = Huffman.ToDictionary(item => item.Letter, item => item.Code);

    /// <summary>The first few JSC words (most common). Anything else is shown as a dictionary number.</summary>
    private static readonly Dictionary<int, string> JscKnown = new()
    {
        [0] = "E", [1] = "T", [2] = "A", [3] = "O", [4] = "I", [5] = "N", [6] = "S", [7] = "H", [8] = "R", [9] = "D",
        [10] = "L", [11] = "C", [12] = "U", [13] = "M", [14] = "W", [15] = "F", [16] = "G", [17] = "Y", [18] = "P",
        [19] = "B", [20] = ",", [21] = ".", [81] = "@ALLCALL", [82] = "BEACON", [99] = "BCNU", [100] = "BD",
        [101] = "BK", [102] = "BN", [103] = "BEAM", [500] = "HOLIDAY", [5000] = "VIRGIN"
    };

    private static readonly int[] JscBase = [0, 7, 70, 637, 5740, 51667, 465010, 4185097];

    /// <summary>Turns one frame into the text a JS8 operator would read.</summary>
    public static string Render(ReadOnlySpan<byte> message, int i3)
    {
        int type = (int)Get(message, 0, 3);
        if (type >= 4)
            return RenderData(message);

        switch (type)
        {
            case 0:
                return RenderHeartbeat(message);
            case 1:
            case 2:
                return RenderCompound(message, type == 2);
            default:
                return RenderDirected(message);
        }
    }

    /// <summary>
    /// Builds one frame from text. Accepts rendered heartbeat/CQ ("K1ABC: @ALLCALL CQ CQ CQ FN42"), a directed line
    /// ("K1ABC: W9XYZ SNR -12"), or free text. A bare "CQ ..." or "TO CMD" line uses <paramref name="myCall"/> as the sender.
    /// </summary>
    public static bool TryPack(string text, string? myCall, Span<byte> message, out int i3)
    {
        message[..9].Clear();
        i3 = TxFirst | TxLast;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        string line = text.Trim().ToUpperInvariant();
        string? from = string.IsNullOrWhiteSpace(myCall) ? null : myCall.Trim().ToUpperInvariant();
        string body = line;
        int colon = line.IndexOf(':');
        if (colon > 0 && TryPackCall(line[..colon].Trim(), out _, out _))
        {
            from = line[..colon].Trim();
            body = line[(colon + 1)..].Trim();
        }

        if (from is not null)
        {
            if (TryPackHeartbeat(from, body, message))
                return true;
            message[..9].Clear();
            if (TryPackDirected(from, body, message))
                return true;
            message[..9].Clear();
        }

        return TryPackData(line, message);
    }

    // ---- rendering ----

    private static string RenderHeartbeat(ReadOnlySpan<byte> message)
    {
        string call = UnpackAlpha50(Get(message, 3, 50));
        int num = (int)((Get(message, 53, 11) << 5) | Get(message, 64, 5));
        int bits3 = (int)Get(message, 69, 3);
        string? grid = UnpackGrid(num & ~AltFlag);
        string tail = grid is null ? "" : " " + grid;
        return (num & AltFlag) != 0
            ? $"{call}: @ALLCALL {CqStrings[bits3]}{tail}"
            : $"{call}: @HB HEARTBEAT{tail}";
    }

    private static string RenderCompound(ReadOnlySpan<byte> message, bool directed)
    {
        string call = UnpackAlpha50(Get(message, 3, 50));
        int num = (int)((Get(message, 53, 11) << 5) | Get(message, 64, 5));
        if (directed)
        {
            int cmd = num - UserGrid;
            string name = cmd is >= 0 and < 32 ? Commands[cmd] : "";
            return $"{call}: {name}".TrimEnd();
        }

        string? grid = UnpackGrid(num & ~AltFlag);
        return $"{call}: {grid}".TrimEnd();
    }

    private static string RenderDirected(ReadOnlySpan<byte> message)
    {
        string from = UnpackCall(Get(message, 3, 28)) + (Get(message, 64, 1) != 0 ? "/P" : "");
        string to = UnpackCall(Get(message, 31, 28)) + (Get(message, 65, 1) != 0 ? "/P" : "");
        int cmd = (int)Get(message, 59, 5);
        int num = (int)Get(message, 66, 6);
        string name = Commands[cmd];
        var builder = new StringBuilder();
        builder.Append(from).Append(": ").Append(to);
        if (cmd == 5)
            builder.Append('>');
        else if (name.Length > 0)
            builder.Append(' ').Append(name);

        if (cmd is 25 or 29)
            builder.Append(' ').Append(FormatSnr(num - 31));
        return builder.ToString();
    }

    private static string RenderData(ReadOnlySpan<byte> message)
    {
        bool compressed = Get(message, 1, 1) != 0;
        var bits = new List<byte>(70);
        for (int i = 2; i < 72; i++)
            bits.Add((byte)Get(message, i, 1));

        // Padding is one zero followed by ones.
        int end = bits.Count;
        while (end > 0 && bits[end - 1] == 1)
            end--;
        if (end > 0)
            end--;
        bits.RemoveRange(end, bits.Count - end);

        return compressed ? RenderJsc(bits) : RenderHuffman(bits);
    }

    private static string RenderHuffman(List<byte> bits)
    {
        var builder = new StringBuilder();
        var code = new StringBuilder();
        foreach (byte bit in bits)
        {
            code.Append(bit == 0 ? '0' : '1');
            if (HuffmanDecode.TryGetValue(code.ToString(), out char letter))
            {
                builder.Append(letter);
                code.Clear();
            }
        }

        return builder.ToString();
    }

    /// <summary>Reads JSC codewords. Known common words print as text. Unknown dictionary entries print as [#index].</summary>
    private static string RenderJsc(List<byte> bits)
    {
        var builder = new StringBuilder();
        int position = 0;
        while (position + 5 <= bits.Count)
        {
            long value = 0;
            int groups = 0;
            bool done = false;
            while (position + 4 <= bits.Count)
            {
                int group = bits[position] << 3 | bits[position + 1] << 2 | bits[position + 2] << 1 | bits[position + 3];
                position += 4;
                if (group >= JscSplit)
                {
                    value = value * JscContinue + (group - JscSplit);
                    groups++;
                    if (groups >= JscBase.Length)
                        return builder.ToString();
                    continue;
                }

                value = value * JscSplit + group + JscBase[groups];
                done = true;
                break;
            }

            if (!done || position >= bits.Count)
                break;

            bool space = bits[position++] != 0;
            builder.Append(JscKnown.TryGetValue((int)value, out string? word) ? word : $"[#{value}]");
            if (space)
                builder.Append(' ');
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatSnr(int snr) => (snr < 0 ? "-" : "+") + Math.Abs(snr).ToString("00");

    // ---- packing ----

    private static bool TryPackHeartbeat(string from, string body, Span<byte> message)
    {
        string[] tokens = body.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return false;

        bool alt;
        int start;
        switch (tokens[0])
        {
            case "@ALLCALL":
            case "CQ":
                alt = true;
                start = tokens[0] == "CQ" ? 0 : 1;
                break;
            case "@HB":
            case "HB":
            case "HEARTBEAT":
                alt = false;
                start = 1;
                break;
            default:
                return false;
        }

        int end = tokens.Length;
        int grid = MaxGrid;
        if (end > start && TryPackGrid(tokens[end - 1], out int packedGrid))
        {
            grid = packedGrid;
            end--;
        }

        string rest = string.Join(' ', tokens[start..end]);
        int bits3 = 0;
        if (alt)
        {
            bits3 = Array.IndexOf(CqStrings, rest);
            if (bits3 < 0)
                return false;
        }
        else if (rest.Length > 0 && rest != "HEARTBEAT" && rest != "HB")
        {
            return false;
        }

        if (!TryPackAlpha50(from, out ulong call))
            return false;

        int num = grid | (alt ? AltFlag : 0);
        Put(message, 0, 3, 0);
        Put(message, 3, 50, call);
        Put(message, 53, 11, (ulong)(num >> 5));
        Put(message, 64, 5, (ulong)(num & 31));
        Put(message, 69, 3, (ulong)bits3);
        return true;
    }

    private static bool TryPackDirected(string from, string body, Span<byte> message)
    {
        if (!TryPackCall(from, out uint fromValue, out bool fromPortable))
            return false;

        int space = body.IndexOf(' ');
        string toText = space < 0 ? body : body[..space];
        string rest = space < 0 ? "" : body[(space + 1)..].Trim();
        if (space < 0 && toText.EndsWith('>'))
        {
            toText = toText[..^1];
            rest = ">";
        }

        if (!TryPackCall(toText, out uint toValue, out bool toPortable))
            return false;

        int cmd = -1;
        int length = -1;
        foreach ((string name, int id) in CommandAliases)
        {
            if (name.Length <= length || !rest.StartsWith(name, StringComparison.Ordinal))
                continue;
            bool boundary = rest.Length == name.Length || rest[name.Length] == ' ' || name[^1] is '?' or '>' or ':';
            if (!boundary)
                continue;
            cmd = id;
            length = name.Length;
        }

        if (cmd < 0)
            return false;

        string tail = rest[length..].Trim();
        int num = 0;
        if (tail.Length > 0)
        {
            if (cmd is not (25 or 29) || !int.TryParse(tail, out int snr) || snr + 31 is < 0 or > 63)
                return false;
            num = snr + 31;
        }

        Put(message, 0, 3, 3);
        Put(message, 3, 28, fromValue);
        Put(message, 31, 28, toValue);
        Put(message, 59, 5, (ulong)cmd);
        Put(message, 64, 1, fromPortable ? 1UL : 0);
        Put(message, 65, 1, toPortable ? 1UL : 0);
        Put(message, 66, 6, (ulong)num);
        return true;
    }

    private static bool TryPackData(string line, Span<byte> message)
    {
        var builder = new StringBuilder();
        foreach (char c in line)
        {
            if (!HuffmanEncode.TryGetValue(c, out string? code))
                return false;
            builder.Append(code);
        }

        // One zero then ones fills the rest, so at least one bit must be left.
        if (builder.Length > 69)
            return false;

        Put(message, 0, 1, 1);
        Put(message, 1, 1, 0);
        int position = 2;
        foreach (char bit in builder.ToString())
            Put(message, position++, 1, bit == '1' ? 1UL : 0);
        position++;
        for (; position < 72; position++)
            Put(message, position, 1, 1);
        return true;
    }

    // ---- callsigns and grids ----

    private static bool TryPackCall(string call, out uint value, out bool portable)
    {
        value = 0;
        portable = false;
        string text = call.Trim().ToUpperInvariant();
        if (text.EndsWith("/P", StringComparison.Ordinal))
        {
            portable = true;
            text = text[..^2];
        }

        int group = Array.IndexOf(BaseCalls, text);
        if (group > 0)
        {
            value = BaseCall + (uint)group;
            return true;
        }

        if (text.Length < 3 || text.Length > 6)
            return false;
        if (char.IsDigit(text[1]) && !char.IsDigit(text[2]))
            text = " " + text;
        if (text.Length > 6 || text.Length < 3)
            return false;
        text = text.PadRight(6);

        if (!char.IsDigit(text[2]) || Alphanumeric.IndexOf(text[0]) is < 0 or > 36 || Alphanumeric.IndexOf(text[1]) is < 0 or > 35)
            return false;

        uint packed = (uint)Alphanumeric.IndexOf(text[0]);
        packed = packed * 36 + (uint)Alphanumeric.IndexOf(text[1]);
        packed = packed * 10 + (uint)(text[2] - '0');
        for (int i = 3; i < 6; i++)
        {
            int letter = text[i] == ' ' ? 26 : text[i] - 'A';
            if (letter is < 0 or > 26)
                return false;
            packed = packed * 27 + (uint)letter;
        }

        value = packed;
        return true;
    }

    private static string UnpackCall(ulong value)
    {
        if (value >= BaseCall)
        {
            int group = (int)(value - BaseCall);
            return group < BaseCalls.Length && group > 0 ? BaseCalls[group] : "<....>";
        }

        uint v = (uint)value;
        var chars = new char[6];
        for (int i = 5; i >= 3; i--)
        {
            uint letter = v % 27;
            chars[i] = letter == 26 ? ' ' : (char)('A' + letter);
            v /= 27;
        }

        chars[2] = (char)('0' + v % 10);
        v /= 10;
        chars[1] = Alphanumeric[(int)(v % 36)];
        v /= 36;
        chars[0] = Alphanumeric[(int)(v % 37)];
        return new string(chars).Trim();
    }

    /// <summary>
    /// 50-bit word for heartbeat and compound frames: three groups of three characters (38 symbols each) plus two flags for
    /// a "/" after the first or second group. Layout is ((g0*2+f0)*B+g1)*2+f1)*B+g2.
    /// </summary>
    private static bool TryPackAlpha50(string word, out ulong value)
    {
        value = 0;
        string text = word.Trim().ToUpperInvariant();
        int slash0 = 0;
        int slash1 = 0;
        if (text.Length >= 4 && text[3] == '/')
        {
            slash0 = 1;
            text = text.Remove(3, 1);
        }

        if (text.Length >= 7 && text[6] == '/')
        {
            slash1 = 1;
            text = text.Remove(6, 1);
        }

        if (text.Length is 0 or > 9)
            return false;
        text = text.PadRight(9);

        var groups = new long[3];
        for (int g = 0; g < 3; g++)
        {
            long packed = 0;
            for (int i = 0; i < 3; i++)
            {
                int index = Alphanumeric.IndexOf(text[g * 3 + i]);
                if (index is < 0 or > 37)
                    return false;
                packed = packed * 38 + index;
            }

            groups[g] = packed;
        }

        value = (ulong)((((groups[0] * 2 + slash0) * GroupBits + groups[1]) * 2 + slash1) * GroupBits + groups[2]);
        return value < 1UL << 50;
    }

    private static string UnpackAlpha50(ulong value)
    {
        long v = (long)value;
        long g2 = v % GroupBits;
        v /= GroupBits;
        int slash1 = (int)(v & 1);
        v >>= 1;
        long g1 = v % GroupBits;
        v /= GroupBits;
        int slash0 = (int)(v & 1);
        v >>= 1;
        long g0 = v % GroupBits;

        var builder = new StringBuilder();
        AppendGroup(builder, g0);
        if (slash0 != 0)
            builder.Append('/');
        AppendGroup(builder, g1);
        if (slash1 != 0)
            builder.Append('/');
        AppendGroup(builder, g2);
        return builder.ToString().TrimEnd();
    }

    private static void AppendGroup(StringBuilder builder, long group)
    {
        Span<char> chars = stackalloc char[3];
        for (int i = 2; i >= 0; i--)
        {
            chars[i] = Alphanumeric[(int)(group % 38)];
            group /= 38;
        }

        builder.Append(chars);
    }

    private static bool TryPackGrid(string grid, out int value)
    {
        value = 0;
        if (grid.Length != 4
            || grid[0] is < 'A' or > 'R' || grid[1] is < 'A' or > 'R'
            || !char.IsAsciiDigit(grid[2]) || !char.IsAsciiDigit(grid[3]))
            return false;

        // Longitude is counted west from 180 at the middle of the square; latitude from -90.
        int west = 179 - 20 * (grid[0] - 'A') - 2 * (grid[2] - '0');
        int latitude = 10 * (grid[1] - 'A') + (grid[3] - '0');
        value = (west + 180) / 2 * 180 + latitude;
        return true;
    }

    private static string? UnpackGrid(int value)
    {
        if (value < 0 || value >= BaseGrid)
            return null;
        int latitude = value % 180;
        int west = 179 - (2 * (value / 180) - 179);
        char[] grid =
        [
            (char)('A' + west / 20), (char)('A' + latitude / 10), (char)('0' + west % 20 / 2), (char)('0' + latitude % 10)
        ];
        return new string(grid);
    }

    // ---- bit helpers (message is 72 bits, most significant bit first) ----

    private static ulong Get(ReadOnlySpan<byte> message, int start, int count)
    {
        ulong value = 0;
        for (int i = 0; i < count; i++)
        {
            int bit = start + i;
            value = (value << 1) | (ulong)(uint)((message[bit >> 3] >> (7 - (bit & 7))) & 1);
        }

        return value;
    }

    private static void Put(Span<byte> message, int start, int count, ulong value)
    {
        for (int i = 0; i < count; i++)
        {
            int bit = start + i;
            byte mask = (byte)(0x80 >> (bit & 7));
            if (((value >> (count - 1 - i)) & 1) != 0)
                message[bit >> 3] |= mask;
            else
                message[bit >> 3] &= (byte)~mask;
        }
    }
}
