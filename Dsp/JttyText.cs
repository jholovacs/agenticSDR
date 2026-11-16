using System.Text;

namespace AgenticSdr.Dsp;

/// <summary>
/// JTTY 32-bit source words: callsign actions (i2=0/1) and five-character free text (i2=3).
/// Structured contest atoms (i2=2) unpack to a short placeholder until a full STRUCT30 table is needed.
/// </summary>
internal static class JttyText
{
    private const uint Tokens = 2_063_592;
    private const uint Max22 = 4_194_304;
    private const string AlnumSpace = " 0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string Alnum = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string Numeric = "0123456789";
    private const string LettersSpace = " ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    private static readonly string[] CallActions0 =
        ["CQ {0} CQ", "{0}", "TU {0} CQ", "{0} TU"];
    private static readonly string[] CallActions1 =
        ["{0} AGN?", "TU NOW {0}"];

    public static bool TryPack(string text, out uint word)
    {
        word = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        string t = Normalize(text);
        if (TryPackCallAction(t, out word))
            return true;
        if (TryPackText5(t, out word))
            return true;
        return false;
    }

    /// <summary>Packs a message into one or more frames (TEXT5 chunks and callsign macros).</summary>
    public static bool TryPackMessage(string text, List<uint> words)
    {
        words.Clear();
        if (string.IsNullOrWhiteSpace(text))
            return false;

        string t = Normalize(text);
        if (TryPackCallAction(t, out uint one))
        {
            words.Add(one);
            return true;
        }

        // Free text: five characters per frame, space-padded on the last.
        if (t.Length == 0 || t.Length > 80)
            return false;
        for (int i = 0; i < t.Length; i += 5)
        {
            string chunk = t[i..Math.Min(i + 5, t.Length)].PadRight(5, ' ');
            if (!TryPackText5(chunk, out uint w))
                return false;
            words.Add(w);
        }

        return words.Count > 0;
    }

    public static bool TryUnpack(uint word, out string text)
    {
        text = "";
        int i2 = (int)(word & 3);
        uint body = word >> 2;
        switch (i2)
        {
            case 0:
            {
                uint call28 = body >> 2;
                int action = (int)(body & 3);
                if (!TryUnpackCall28(call28, out string call))
                    return false;
                text = string.Format(CallActions0[action], call);
                return true;
            }
            case 1:
            {
                uint call28 = body >> 2;
                int action = (int)(body & 3);
                if (action > 1 || !TryUnpackCall28(call28, out string call))
                    return false;
                text = string.Format(CallActions1[action], call);
                return true;
            }
            case 2:
                // STRUCT30 needs family/subtype tables; reject for now so CRC collisions do not print junk.
                return false;
            case 3:
                return TryUnpackText5(body, out text);
            default:
                return false;
        }
    }

    public static string RenderMessage(IReadOnlyList<(uint Word, bool Eom)> frames)
    {
        var sb = new StringBuilder();
        foreach ((uint word, bool _) in frames)
        {
            if (!TryUnpack(word, out string part))
                continue;
            int i2 = (int)(word & 3);
            if (i2 == 3)
            {
                // Keep per-frame trailing spaces so "SE - " + "NO FA" stays "SE - NO FA".
                sb.Append(part);
            }
            else
            {
                if (sb.Length > 0 && sb[^1] != ' ')
                    sb.Append(' ');
                sb.Append(part);
                sb.Append(' ');
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static bool TryPackCallAction(string text, out uint word)
    {
        word = 0;
        // CQ CALL CQ
        if (text.StartsWith("CQ ", StringComparison.Ordinal) && text.EndsWith(" CQ", StringComparison.Ordinal))
        {
            string call = text[3..^3].Trim();
            if (TryPackCall28(call, out uint c28))
            {
                word = (c28 << 4) | (0u << 2) | 0u;
                return true;
            }
        }

        if (text.StartsWith("TU ", StringComparison.Ordinal) && text.EndsWith(" CQ", StringComparison.Ordinal))
        {
            string call = text[3..^3].Trim();
            if (TryPackCall28(call, out uint c28))
            {
                word = (c28 << 4) | (2u << 2) | 0u;
                return true;
            }
        }

        if (text.StartsWith("TU NOW ", StringComparison.Ordinal))
        {
            string call = text[7..].Trim();
            if (TryPackCall28(call, out uint c28))
            {
                word = (c28 << 4) | (1u << 2) | 1u;
                return true;
            }
        }

        if (text.EndsWith(" AGN?", StringComparison.Ordinal))
        {
            string call = text[..^5].Trim();
            if (TryPackCall28(call, out uint c28))
            {
                word = (c28 << 4) | (0u << 2) | 1u;
                return true;
            }
        }

        if (text.EndsWith(" TU", StringComparison.Ordinal))
        {
            string call = text[..^3].Trim();
            if (TryPackCall28(call, out uint c28))
            {
                word = (c28 << 4) | (3u << 2) | 0u;
                return true;
            }
        }

        if (TryPackCall28(text, out uint bare))
        {
            word = (bare << 4) | (1u << 2) | 0u;
            return true;
        }

        return false;
    }

    private static bool TryPackText5(string five, out uint word)
    {
        word = 0;
        if (five.Length != 5)
            return false;
        uint body = 0;
        for (int i = 0; i < 5; i++)
        {
            int idx = JttyTables.Alphabet.IndexOf(five[i]);
            if (idx < 0)
                return false;
            body = (body << 6) | (uint)idx;
        }

        word = (body << 2) | 3u;
        return true;
    }

    private static bool TryUnpackText5(uint body30, out string text)
    {
        Span<char> chars = stackalloc char[5];
        for (int i = 4; i >= 0; i--)
        {
            int idx = (int)(body30 & 63);
            body30 >>= 6;
            if ((uint)idx >= (uint)JttyTables.Alphabet.Length)
            {
                text = "";
                return false;
            }

            chars[i] = JttyTables.Alphabet[idx];
        }

        text = new string(chars);
        return true;
    }

    private static bool TryPackCall28(string callsign, out uint packed)
    {
        packed = 0;
        int n = PackBaseCall(callsign);
        if (n < 0)
            return false;
        packed = Tokens + Max22 + (uint)n;
        return packed < (1u << 28);
    }

    private static bool TryUnpackCall28(uint value, out string call)
    {
        call = "";
        if (value < Tokens + Max22)
            return false;
        uint body = value - Max22 - Tokens;
        Span<char> chars = stackalloc char[6];
        chars[5] = CharAt((int)(body % 27), LettersSpace);
        body /= 27;
        chars[4] = CharAt((int)(body % 27), LettersSpace);
        body /= 27;
        chars[3] = CharAt((int)(body % 27), LettersSpace);
        body /= 27;
        chars[2] = CharAt((int)(body % 10), Numeric);
        body /= 10;
        chars[1] = CharAt((int)(body % 36), Alnum);
        body /= 36;
        chars[0] = CharAt((int)(body % 37), AlnumSpace);
        string text = new string(chars);
        if (text.StartsWith("3D0", StringComparison.Ordinal) && text.Length > 3 && text[3] != ' ')
            call = "3DA0" + text[3..].Trim();
        else if (text[0] == 'Q' && text[1] is >= 'A' and <= 'Z')
            call = "3X" + text[1..].Trim();
        else
            call = text.Trim();
        return call.Length >= 3;
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

        int i0 = AlnumSpace.IndexOf(c6[0]);
        int i1 = Alnum.IndexOf(c6[1]);
        int i2 = Numeric.IndexOf(c6[2]);
        int i3 = LettersSpace.IndexOf(c6[3]);
        int i4 = LettersSpace.IndexOf(c6[4]);
        int i5 = LettersSpace.IndexOf(c6[5]);
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

    private static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool space = false;
        foreach (char ch in text.Trim().ToUpperInvariant())
        {
            char c = ch;
            if (JttyTables.Alphabet.IndexOf(c) < 0)
                c = '#';
            if (c == ' ')
            {
                space = true;
                continue;
            }

            if (space && sb.Length > 0)
                sb.Append(' ');
            space = false;
            sb.Append(c);
        }

        return sb.ToString();
    }

    private static char CharAt(int index, string alphabet) =>
        (uint)index < (uint)alphabet.Length ? alphabet[index] : '?';
}
