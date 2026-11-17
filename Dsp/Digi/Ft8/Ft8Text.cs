using System.Text;

namespace AgenticSdr.Dsp;

/// <summary>
/// Remembers callsigns so a later FT8 message can show a hashed call as text.
/// The decoder hashes long callsigns to save bits. A call already seen in plain text is filled back in.
/// </summary>
public sealed class Ft8Callsigns
{
    public static Ft8Callsigns Shared { get; } = new();

    private readonly object _gate = new();
    private readonly Dictionary<int, string> _n22 = [];
    private readonly Dictionary<int, string> _n12 = [];
    private readonly Dictionary<int, string> _n10 = [];

    public void Remember(string callsign)
    {
        if (!Hash(callsign, out int n22, out int n12, out int n10))
            return;
        lock (_gate)
        {
            _n22[n22] = callsign;
            _n12[n12] = callsign;
            _n10[n10] = callsign;
        }
    }

    public string Lookup22(int hash) => Lookup(_n22, hash);

    public string Lookup12(int hash) => Lookup(_n12, hash);

    public string Lookup10(int hash) => Lookup(_n10, hash);

    private string Lookup(Dictionary<int, string> table, int hash)
    {
        lock (_gate)
            return table.TryGetValue(hash, out string? call) ? $"<{call}>" : "<...>";
    }

    public static bool Hash(string callsign, out int n22, out int n12, out int n10)
    {
        n22 = n12 = n10 = 0;
        ulong packed = 0;
        int length = 0;
        foreach (char raw in callsign)
        {
            if (length == 11)
                break;
            int index = Ft8Text.IndexOf(raw, Ft8Alphabet.AlnumSpaceSlash);
            if (index < 0)
                return false;
            packed = 38 * packed + (ulong)index;
            length++;
        }

        while (length < 11)
        {
            packed *= 38;
            length++;
        }

        n22 = (int)(((47055833459ul * packed) >> 42) & 0x3FFFFF);
        n12 = n22 >> 10;
        n10 = n22 >> 12;
        return true;
    }
}

/// <summary>The 42 characters FT8 uses for free-text messages.</summary>
public enum Ft8Alphabet
{
    Full,
    AlnumSpaceSlash,
    AlnumSpace,
    LettersSpace,
    Alnum,
    Numeric
}

/// <summary>
/// Packs a human FT8 line into the 77-bit message, and unpacks those bits back into text.
/// Standard messages are callsigns, grids, and reports. Free text is the short leftover form.
/// </summary>
public static class Ft8Text
{
    private const uint Tokens = 2_063_592;
    private const uint Max22 = 4_194_304;
    private const ushort MaxGrid4 = 32_400;

    public static bool TryPack(string message, Span<byte> payload)
    {
        if (payload.Length < 10 || string.IsNullOrWhiteSpace(message))
            return false;

        payload[..10].Clear();
        string text = Collapse(message);
        string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is >= 2 and <= 4 && TryPackStructured(parts, payload))
            return true;

        return parts.Length <= 4 && TryPackFree(text, payload);
    }

    public static string? Unpack(ReadOnlySpan<byte> payload, Ft8Callsigns? calls = null)
    {
        if (payload.Length < 10)
            return null;

        calls ??= new Ft8Callsigns();
        int i3 = (payload[9] >> 3) & 0x07;
        int n3 = ((payload[8] << 2) & 0x04) | ((payload[9] >> 6) & 0x03);
        return i3 switch
        {
            0 when n3 == 0 => UnpackFree(payload),
            0 when n3 == 5 => UnpackTelemetry(payload),
            1 or 2 => UnpackStandard(payload, i3, calls),
            4 => UnpackNonstandard(payload, calls),
            _ => $"FT8 type {i3}.{n3}"
        };
    }

    internal static int IndexOf(char c, Ft8Alphabet table)
    {
        int n = 0;
        if (table is not Ft8Alphabet.Alnum and not Ft8Alphabet.Numeric)
        {
            if (c == ' ')
                return 0;
            n += 1;
        }

        if (table != Ft8Alphabet.LettersSpace)
        {
            if (c is >= '0' and <= '9')
                return n + (c - '0');
            n += 10;
        }

        if (table != Ft8Alphabet.Numeric)
        {
            if (c is >= 'A' and <= 'Z')
                return n + (c - 'A');
            n += 26;
        }

        return table switch
        {
            Ft8Alphabet.Full when c == '+' => n,
            Ft8Alphabet.Full when c == '-' => n + 1,
            Ft8Alphabet.Full when c == '.' => n + 2,
            Ft8Alphabet.Full when c == '/' => n + 3,
            Ft8Alphabet.Full when c == '?' => n + 4,
            Ft8Alphabet.AlnumSpaceSlash when c == '/' => n,
            _ => -1
        };
    }

    private static bool TryPackStructured(string[] parts, Span<byte> payload)
    {
        string callTo;
        string callDe;
        string extra;
        int index;
        if (parts[0] == "CQ")
        {
            if (parts.Length >= 2 && CqModifier(parts[1]) >= 0)
            {
                callTo = "CQ " + parts[1];
                index = 2;
            }
            else
            {
                callTo = "CQ";
                index = 1;
            }
        }
        else
        {
            callTo = parts[0];
            index = 1;
        }

        if (index >= parts.Length)
            return false;
        callDe = parts[index++];
        extra = index < parts.Length ? parts[index++] : "";
        if (index != parts.Length)
            return false;

        return TryPackStandard(callTo, callDe, extra, payload)
            || TryPackNonstandard(callTo, callDe, extra, payload);
    }

    private static bool TryPackStandard(string callTo, string callDe, string extra, Span<byte> payload)
    {
        if (Pack28(callTo, out uint n28A, out int suffixA) < 0)
            return false;
        if (Pack28(callDe, out uint n28B, out int suffixB) < 0)
            return false;

        int i3 = 1;
        bool slashP = callTo.EndsWith("/P", StringComparison.Ordinal) || callDe.EndsWith("/P", StringComparison.Ordinal);
        bool slashR = callTo.EndsWith("/R", StringComparison.Ordinal) || callDe.EndsWith("/R", StringComparison.Ordinal);
        if (slashP && slashR)
            return false;
        if (slashP)
            i3 = 2;

        bool cq = callTo == "CQ" || callTo.StartsWith("CQ ", StringComparison.Ordinal);
        int slash = callDe.IndexOf('/');
        if (slash >= 2 && cq && callDe is not "/P" && !callDe.EndsWith("/P", StringComparison.Ordinal) && !callDe.EndsWith("/R", StringComparison.Ordinal))
            return false;

        if (!TryPackGrid(extra, out uint grid))
            return false;

        uint n29A = (n28A << 1) | (uint)suffixA;
        uint n29B = (n28B << 1) | (uint)suffixB;
        if (callTo.EndsWith("/R", StringComparison.Ordinal) || callTo.EndsWith("/P", StringComparison.Ordinal))
            n29A |= 1;
        if (callDe.EndsWith("/R", StringComparison.Ordinal) || callDe.EndsWith("/P", StringComparison.Ordinal))
            n29B |= 1;

        payload[0] = (byte)(n29A >> 21);
        payload[1] = (byte)(n29A >> 13);
        payload[2] = (byte)(n29A >> 5);
        payload[3] = (byte)((n29A << 3) | (n29B >> 26));
        payload[4] = (byte)(n29B >> 18);
        payload[5] = (byte)(n29B >> 10);
        payload[6] = (byte)(n29B >> 2);
        payload[7] = (byte)((n29B << 6) | (grid >> 10));
        payload[8] = (byte)(grid >> 2);
        payload[9] = (byte)((grid << 6) | ((uint)i3 << 3));
        return true;
    }

    private static bool TryPackNonstandard(string callTo, string callDe, string extra, Span<byte> payload)
    {
        bool cq = callTo == "CQ" || callTo.StartsWith("CQ ", StringComparison.Ordinal);
        if (!cq && callTo.Length < 3)
            return false;
        if (callDe.Length < 3)
            return false;

        int flip = 0;
        int n12 = 0;
        string plain = callDe;
        if (!cq)
        {
            bool hashedTo = callTo.StartsWith('<') && callTo.EndsWith('>');
            flip = hashedTo ? 1 : 0;
            string hashed = flip == 0 ? callTo : callDe;
            plain = flip == 0 ? callDe : callTo;
            string bare = hashed.Trim('<', '>');
            if (!Ft8Callsigns.Hash(bare, out int n22, out n12, out _))
                return false;
            _ = n22;
        }

        if (!TryPack58(plain, out ulong n58))
            return false;

        int report = extra switch
        {
            "RRR" => 1,
            "RR73" => 2,
            "73" => 3,
            _ => 0
        };
        int icq = cq ? 1 : 0;
        payload[0] = (byte)(n12 >> 4);
        payload[1] = (byte)((n12 << 4) | (int)(n58 >> 54));
        payload[2] = (byte)(n58 >> 46);
        payload[3] = (byte)(n58 >> 38);
        payload[4] = (byte)(n58 >> 30);
        payload[5] = (byte)(n58 >> 22);
        payload[6] = (byte)(n58 >> 14);
        payload[7] = (byte)(n58 >> 6);
        payload[8] = (byte)(((uint)n58 << 2) | (uint)(flip << 1) | (uint)(report >> 1));
        payload[9] = (byte)((report << 7) | (icq << 6) | (4 << 3));
        return true;
    }

    private static string? UnpackStandard(ReadOnlySpan<byte> payload, int i3, Ft8Callsigns calls)
    {
        uint n29A = (uint)((payload[0] << 21) | (payload[1] << 13) | (payload[2] << 5) | (payload[3] >> 3));
        uint n29B = ((uint)(payload[3] & 0x07) << 26)
            | (uint)(payload[4] << 18)
            | (uint)(payload[5] << 10)
            | (uint)(payload[6] << 2)
            | (uint)(payload[7] >> 6);
        int roger = (payload[7] & 0x20) >> 5;
        int grid = ((payload[7] & 0x1F) << 10) | (payload[8] << 2) | (payload[9] >> 6);
        if (!Unpack28(n29A >> 1, (int)(n29A & 1), i3, calls, out string callTo))
            return null;
        if (!Unpack28(n29B >> 1, (int)(n29B & 1), i3, calls, out string callDe))
            return null;
        if (!UnpackGrid(grid, roger, out string extra))
            return null;
        return Join(callTo, callDe, extra);
    }

    private static string UnpackNonstandard(ReadOnlySpan<byte> payload, Ft8Callsigns calls)
    {
        int n12 = (payload[0] << 4) | (payload[1] >> 4);
        ulong n58 = ((ulong)(payload[1] & 0x0F) << 54)
            | ((ulong)payload[2] << 46)
            | ((ulong)payload[3] << 38)
            | ((ulong)payload[4] << 30)
            | ((ulong)payload[5] << 22)
            | ((ulong)payload[6] << 14)
            | ((ulong)payload[7] << 6)
            | ((ulong)payload[8] >> 2);
        int flip = (payload[8] >> 1) & 1;
        int report = ((payload[8] & 1) << 1) | (payload[9] >> 7);
        int cq = (payload[9] >> 6) & 1;
        string plain = Unpack58(n58, calls);
        string hashed = calls.Lookup12(n12);
        string callTo;
        string callDe;
        string extra;
        if (cq == 0)
        {
            callTo = flip == 0 ? hashed : plain;
            callDe = flip == 0 ? plain : hashed;
            extra = report switch
            {
                1 => "RRR",
                2 => "RR73",
                3 => "73",
                _ => ""
            };
        }
        else
        {
            callTo = "CQ";
            callDe = plain;
            extra = "";
        }

        return Join(callTo, callDe, extra);
    }

    private static string Join(string callTo, string callDe, string extra)
    {
        if (extra.Length == 0)
            return $"{callTo} {callDe}";
        return $"{callTo} {callDe} {extra}";
    }

    private static int Pack28(string callsign, out uint packed, out int suffix)
    {
        packed = 0;
        suffix = 0;
        if (callsign == "DE")
            return 0;
        if (callsign == "QRZ")
        {
            packed = 1;
            return 1;
        }

        if (callsign == "CQ")
        {
            packed = 2;
            return 2;
        }

        if (callsign.StartsWith("CQ ", StringComparison.Ordinal) && callsign.Length < 8)
        {
            int modifier = CqModifier(callsign[3..]);
            if (modifier < 0)
                return -1;
            packed = (uint)(3 + modifier);
            return (int)packed;
        }

        string baseCall = callsign;
        if (callsign.EndsWith("/P", StringComparison.Ordinal) || callsign.EndsWith("/R", StringComparison.Ordinal))
        {
            suffix = 1;
            baseCall = callsign[..^2];
        }

        int standard = PackBaseCall(baseCall);
        if (standard >= 0)
        {
            packed = Tokens + Max22 + (uint)standard;
            return (int)packed;
        }

        if (baseCall.Length is >= 3 and <= 11 && Ft8Callsigns.Hash(baseCall, out int n22, out _, out _))
        {
            suffix = 0;
            packed = Tokens + (uint)n22;
            return (int)packed;
        }

        return -1;
    }

    private static bool Unpack28(uint value, int suffix, int i3, Ft8Callsigns calls, out string call)
    {
        call = "";
        if (value < Tokens)
        {
            if (value <= 2)
            {
                call = value switch
                {
                    0 => "DE",
                    1 => "QRZ",
                    _ => "CQ"
                };
                return true;
            }

            if (value <= 1002)
            {
                call = "CQ " + (value - 3).ToString("000");
                return true;
            }

            if (value <= 532443)
            {
                uint n = value - 1003;
                Span<char> letters = stackalloc char[4];
                for (int i = 3; i >= 0; i--)
                {
                    letters[i] = Character((int)(n % 27), Ft8Alphabet.LettersSpace);
                    n /= 27;
                }

                call = "CQ " + new string(letters).Trim();
                return true;
            }

            return false;
        }

        value -= Tokens;
        if (value < Max22)
        {
            call = calls.Lookup22((int)value);
            return true;
        }

        uint body = value - Max22;
        Span<char> chars = stackalloc char[6];
        chars[5] = Character((int)(body % 27), Ft8Alphabet.LettersSpace);
        body /= 27;
        chars[4] = Character((int)(body % 27), Ft8Alphabet.LettersSpace);
        body /= 27;
        chars[3] = Character((int)(body % 27), Ft8Alphabet.LettersSpace);
        body /= 27;
        chars[2] = Character((int)(body % 10), Ft8Alphabet.Numeric);
        body /= 10;
        chars[1] = Character((int)(body % 36), Ft8Alphabet.Alnum);
        body /= 36;
        chars[0] = Character((int)(body % 37), Ft8Alphabet.AlnumSpace);
        string text = new string(chars);
        if (text.StartsWith("3D0", StringComparison.Ordinal) && text.Length > 3 && text[3] != ' ')
            call = "3DA0" + text[3..].Trim();
        else if (text[0] == 'Q' && text[1] is >= 'A' and <= 'Z')
            call = "3X" + text[1..].Trim();
        else
            call = text.Trim();

        if (call.Length < 3)
            return false;
        if (suffix != 0)
            call += i3 == 2 ? "/P" : "/R";

        calls.Remember(call);
        return true;
    }

    private static int PackBaseCall(string callsign)
    {
        if (callsign.Length is < 3 or > 7)
            return -1;

        Span<char> c6 = [' ', ' ', ' ', ' ', ' ', ' '];
        if (callsign.StartsWith("3DA0", StringComparison.Ordinal) && callsign.Length is > 4 and <= 7)
        {
            "3D0".AsSpan().CopyTo(c6);
            callsign.AsSpan(4).CopyTo(c6[3..]);
        }
        else if (callsign.StartsWith("3X", StringComparison.Ordinal) && callsign.Length >= 3 && callsign[2] is >= 'A' and <= 'Z' && callsign.Length <= 7)
        {
            c6[0] = 'Q';
            callsign.AsSpan(2).CopyTo(c6[1..]);
        }
        else if (callsign.Length >= 3 && callsign[2] is >= '0' and <= '9' && callsign.Length <= 6)
        {
            callsign.AsSpan().CopyTo(c6);
        }
        else if (callsign.Length >= 2 && callsign[1] is >= '0' and <= '9' && callsign.Length <= 5)
        {
            callsign.AsSpan().CopyTo(c6[1..]);
        }
        else
        {
            return -1;
        }

        int i0 = IndexOf(c6[0], Ft8Alphabet.AlnumSpace);
        int i1 = IndexOf(c6[1], Ft8Alphabet.Alnum);
        int i2 = IndexOf(c6[2], Ft8Alphabet.Numeric);
        int i3 = IndexOf(c6[3], Ft8Alphabet.LettersSpace);
        int i4 = IndexOf(c6[4], Ft8Alphabet.LettersSpace);
        int i5 = IndexOf(c6[5], Ft8Alphabet.LettersSpace);
        if (i0 < 0 || i1 < 0 || i2 < 0 || i3 < 0 || i4 < 0 || i5 < 0)
            return -1;

        int n = i0;
        n = n * 36 + i1;
        n = n * 10 + i2;
        n = n * 27 + i3;
        n = n * 27 + i4;
        n = n * 27 + i5;
        return n;
    }

    private static int CqModifier(string token)
    {
        if (token.Length is 0 or > 4)
            return -1;
        int digits = 0;
        int letters = 0;
        int packed = 0;
        foreach (char c in token)
        {
            if (c is >= '0' and <= '9')
                digits++;
            else if (c is >= 'A' and <= 'Z')
            {
                letters++;
                packed = 27 * packed + (c - 'A' + 1);
            }
            else
            {
                return -1;
            }
        }

        if (digits == 3 && letters == 0 && token.Length == 3)
            return int.Parse(token);
        if (digits == 0 && letters is >= 1 and <= 4)
            return 1000 + packed;
        return -1;
    }

    private static bool TryPackGrid(string grid, out uint packed)
    {
        packed = MaxGrid4 + 1;
        if (grid.Length == 0)
            return true;
        if (grid == "RRR")
        {
            packed = MaxGrid4 + 2;
            return true;
        }

        if (grid == "RR73")
        {
            packed = MaxGrid4 + 3;
            return true;
        }

        if (grid == "73")
        {
            packed = MaxGrid4 + 4;
            return true;
        }

        if (grid.Length >= 4
            && grid[0] is >= 'A' and <= 'R'
            && grid[1] is >= 'A' and <= 'R'
            && grid[2] is >= '0' and <= '9'
            && grid[3] is >= '0' and <= '9')
        {
            packed = (uint)(((((grid[0] - 'A') * 18) + (grid[1] - 'A')) * 10 + (grid[2] - '0')) * 10 + (grid[3] - '0'));
            return true;
        }

        bool roger = grid[0] == 'R';
        string number = roger ? grid[1..] : grid;
        if (!TryParseReport(number, out int report))
            return false;
        packed = (uint)(MaxGrid4 + 35 + report);
        if (roger)
            packed |= 0x8000;
        return true;
    }

    private static bool UnpackGrid(int grid, int roger, out string extra)
    {
        extra = "";
        if (grid <= MaxGrid4)
        {
            int n = grid;
            var chars = new char[4];
            chars[3] = (char)('0' + (n % 10));
            n /= 10;
            chars[2] = (char)('0' + (n % 10));
            n /= 10;
            chars[1] = (char)('A' + (n % 18));
            n /= 18;
            chars[0] = (char)('A' + (n % 18));
            extra = roger > 0 ? "R " + new string(chars) : new string(chars);
            return true;
        }

        int kind = grid - MaxGrid4;
        extra = kind switch
        {
            1 => "",
            2 => "RRR",
            3 => "RR73",
            4 => "73",
            _ => (roger > 0 ? "R" : "") + FormatReport(kind - 35)
        };
        return true;
    }

    private static bool TryParseReport(string text, out int value)
    {
        value = 0;
        if (text.Length is 0 or > 4)
            return false;
        int sign = 1;
        int index = 0;
        if (text[0] == '-')
        {
            sign = -1;
            index = 1;
        }
        else if (text[0] == '+')
        {
            index = 1;
        }

        if (index >= text.Length)
            return false;
        int parsed = 0;
        for (; index < text.Length; index++)
        {
            if (text[index] is < '0' or > '9')
                return false;
            parsed = parsed * 10 + (text[index] - '0');
        }

        value = sign * parsed;
        return value is >= -30 and <= 60;
    }

    private static string FormatReport(int value) =>
        value < 0 ? $"-{Math.Abs(value):00}" : $"+{value:00}";

    private static bool TryPack58(string callsign, out ulong packed)
    {
        packed = 0;
        string text = callsign.Trim('<', '>');
        if (text.Length is < 3 or > 11)
            return false;
        foreach (char c in text)
        {
            int index = IndexOf(c, Ft8Alphabet.AlnumSpaceSlash);
            if (index < 0)
                return false;
            packed = packed * 38 + (ulong)index;
        }

        return true;
    }

    private static string Unpack58(ulong packed, Ft8Callsigns calls)
    {
        Span<char> chars = stackalloc char[11];
        for (int i = 10; i >= 0; i--)
        {
            chars[i] = Character((int)(packed % 38), Ft8Alphabet.AlnumSpaceSlash);
            packed /= 38;
        }

        string call = new string(chars).Trim();
        if (call.Length >= 3)
            calls.Remember(call);
        return call;
    }

    private static bool TryPackFree(string text, Span<byte> payload)
    {
        if (text.Length is 0 or > 13)
            return false;
        Span<byte> body = stackalloc byte[9];
        foreach (char c in text.PadRight(13))
        {
            int index = IndexOf(c, Ft8Alphabet.Full);
            if (index < 0)
                return false;
            int carry = index;
            for (int i = 8; i >= 0; i--)
            {
                carry += body[i] * 42;
                body[i] = (byte)carry;
                carry >>= 8;
            }
        }

        int bit = 0;
        for (int i = 8; i >= 0; i--)
        {
            payload[i] = (byte)((body[i] << 1) | (bit >> 7));
            bit = body[i] & 0x80;
        }

        payload[9] = 0;
        return true;
    }

    private static string UnpackFree(ReadOnlySpan<byte> payload)
    {
        Span<byte> body = stackalloc byte[9];
        int carry = 0;
        for (int i = 0; i < 9; i++)
        {
            body[i] = (byte)((carry << 7) | (payload[i] >> 1));
            carry = payload[i] & 1;
        }

        Span<char> chars = stackalloc char[13];
        for (int index = 12; index >= 0; index--)
        {
            int remainder = 0;
            for (int i = 0; i < 9; i++)
            {
                remainder = (remainder << 8) | body[i];
                body[i] = (byte)(remainder / 42);
                remainder %= 42;
            }

            chars[index] = Character(remainder, Ft8Alphabet.Full);
        }

        return new string(chars).Trim();
    }

    private static string UnpackTelemetry(ReadOnlySpan<byte> payload)
    {
        var text = new StringBuilder(18);
        int carry = 0;
        for (int i = 0; i < 9; i++)
        {
            int value = (carry << 7) | (payload[i] >> 1);
            carry = payload[i] & 1;
            text.Append(value.ToString("X2"));
        }

        return text.ToString();
    }

    private static char Character(int index, Ft8Alphabet table)
    {
        int c = index;
        if (table is not Ft8Alphabet.Alnum and not Ft8Alphabet.Numeric)
        {
            if (c == 0)
                return ' ';
            c -= 1;
        }

        if (table != Ft8Alphabet.LettersSpace)
        {
            if (c < 10)
                return (char)('0' + c);
            c -= 10;
        }

        if (table != Ft8Alphabet.Numeric && c < 26)
            return (char)('A' + c);
        if (table != Ft8Alphabet.Numeric)
            c -= 26;

        return table switch
        {
            Ft8Alphabet.Full when c is >= 0 and < 5 => "+-./?"[c],
            Ft8Alphabet.AlnumSpaceSlash when c == 0 => '/',
            _ => ' '
        };
    }

    private static string Collapse(string message)
    {
        var builder = new StringBuilder(message.Length);
        bool space = false;
        foreach (char raw in message.Trim())
        {
            char c = char.ToUpperInvariant(raw);
            if (c == ' ')
            {
                space = builder.Length > 0;
                continue;
            }

            if (space)
                builder.Append(' ');
            builder.Append(c);
            space = false;
        }

        return builder.ToString();
    }
}
