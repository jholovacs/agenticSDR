using System.Text;

namespace AgenticSdr.Dsp;

/// <summary>
/// Reads D-STAR DV signalling: radio-header callsigns (MY / UR / RPT1 / RPT2) and slow-data text.
/// AMBE voice frames are not turned into speech.
/// </summary>
public sealed class DstarReader
{
    private const int MaxBitBuffer = 8_000;
    private readonly List<int> _bits = new(2_048);
    private readonly List<int> _bitsInv = new(2_048);
    private string? _lastHeader;
    private string? _lastMessage;

    public IReadOnlyList<string> Push(ReadOnlySpan<System.Numerics.Complex> iq, double sampleRate, double offsetHz)
    {
        int[] recovered = DstarSync.BitStream(iq, sampleRate, offsetHz);
        if (recovered.Length == 0)
            return [];

        var lines = new List<string>();
        Append(_bits, recovered);
        Append(_bitsInv, DstarSync.Invert(recovered));
        DecodeBuffer(_bits, lines);
        DecodeBuffer(_bitsInv, lines);
        return lines;
    }

    /// <summary>Encode a Type-1 radio header to 660 on-air bits (for tests / synthetic IQ).</summary>
    public static int[] EncodeHeader(byte flag1, string rpt2, string rpt1, string ur, string my, string suffix)
    {
        var raw = new byte[41];
        raw[0] = flag1;
        raw[1] = 0;
        raw[2] = 0;
        WriteCall(raw.AsSpan(3, 8), rpt2);
        WriteCall(raw.AsSpan(11, 8), rpt1);
        WriteCall(raw.AsSpan(19, 8), ur);
        WriteCall(raw.AsSpan(27, 8), my);
        WriteCall(raw.AsSpan(35, 4), suffix);
        ushort crc = CrcX25(raw.AsSpan(0, 39));
        raw[39] = (byte)(crc & 0xFF);
        raw[40] = (byte)(crc >> 8);

        var info = new int[330];
        int[] payload = DstarSync.BytesToBits(raw);
        Array.Copy(payload, info, 328);
        info[328] = 0;
        info[329] = 0;
        int[] coded = Convolve(info);
        return Scramble(Interleave(coded));
    }

    private void DecodeBuffer(List<int> buffer, List<string> lines)
    {
        int[] bits = buffer.ToArray();
        int syncAt = DstarSync.FindPattern(bits, DstarSync.FrameSync, maxErr: 1);
        if (syncAt < 0)
            return;

        int headerAt = syncAt + DstarSync.FrameSync.Length;
        if (headerAt + DstarSync.HeaderCodedBits > bits.Length)
            return;

        if (!TryParseHeader(bits.AsSpan(headerAt, DstarSync.HeaderCodedBits), out DstarHeader header))
            return;

        string note = FormatHeader(header);
        if (!string.Equals(note, _lastHeader, StringComparison.Ordinal))
        {
            _lastHeader = note;
            lines.Add(note);
        }

        int consumed = headerAt + DstarSync.HeaderCodedBits;
        ReadSlowData(bits.AsSpan(consumed), lines);
        buffer.RemoveRange(0, Math.Min(consumed, buffer.Count));
    }

    private void ReadSlowData(ReadOnlySpan<int> afterHeader, List<string> lines)
    {
        var fields = new List<byte[]>(24);
        int[] end = EndPattern();
        for (int at = 0; at + DstarSync.FrameBits <= afterHeader.Length; at += DstarSync.FrameBits)
        {
            int endProbe = Math.Min(48, afterHeader.Length - at);
            if (DstarSync.FindPattern(afterHeader.Slice(at, endProbe), end, maxErr: 4) == 0)
                break;

            int frameIndex = at / DstarSync.FrameBits;
            ReadOnlySpan<int> dataBits = afterHeader.Slice(at + 72, 24);
            if (frameIndex % 21 == 0 || IsDataSync(dataBits))
                continue;

            fields.Add(BitsToBytes3(dataBits));
            if (fields.Count >= 8 && TryAssembleMessage(fields, out string message))
            {
                string note = $"D-STAR message: {message}";
                if (!string.Equals(note, _lastMessage, StringComparison.Ordinal))
                {
                    _lastMessage = note;
                    lines.Add(note);
                }
                break;
            }
        }
    }

    private static bool TryAssembleMessage(List<byte[]> fields, out string message)
    {
        message = "";
        var scratch = new char[20];
        Array.Fill(scratch, ' ');
        bool any = false;
        for (int i = 0; i + 1 < fields.Count; i += 2)
        {
            byte[] a = UnscrambleSlow(fields[i]);
            byte[] b = UnscrambleSlow(fields[i + 1]);
            byte type = a[0];
            if ((type & 0xF0) != 0x40)
                continue;
            int index = type & 0x0F;
            if (index > 3)
                continue;
            scratch[index * 5] = Printable((char)a[1]);
            scratch[index * 5 + 1] = Printable((char)a[2]);
            scratch[index * 5 + 2] = Printable((char)b[0]);
            scratch[index * 5 + 3] = Printable((char)b[1]);
            scratch[index * 5 + 4] = Printable((char)b[2]);
            any = true;
        }

        if (!any)
            return false;
        message = new string(scratch).TrimEnd();
        return message.Length > 0;
    }

    private static void Append(List<int> buffer, ReadOnlySpan<int> bits)
    {
        buffer.AddRange(bits);
        if (buffer.Count > MaxBitBuffer)
            buffer.RemoveRange(0, buffer.Count - MaxBitBuffer);
    }

    private static bool TryParseHeader(ReadOnlySpan<int> coded660, out DstarHeader header)
    {
        header = default;
        int[] descrambled = Scramble(coded660);
        int[] deinterleaved = Deinterleave(descrambled);
        int[] decoded = Viterbi(deinterleaved);
        if (decoded.Length < 328)
            return false;

        byte[] raw = BitsToBytes(decoded.AsSpan(0, 328));
        ushort expect = CrcX25(raw.AsSpan(0, 39));
        ushort got = (ushort)(raw[39] | (raw[40] << 8));
        if (expect != got)
            return false;

        header = new DstarHeader(
            raw[0],
            ReadCall(raw.AsSpan(3, 8)),
            ReadCall(raw.AsSpan(11, 8)),
            ReadCall(raw.AsSpan(19, 8)),
            ReadCall(raw.AsSpan(27, 8)),
            ReadCall(raw.AsSpan(35, 4)));
        return true;
    }

    private static string FormatHeader(DstarHeader h)
    {
        string my = string.IsNullOrEmpty(h.Suffix) ? h.My : $"{h.My}/{h.Suffix}";
        var sb = new StringBuilder();
        sb.Append("D-STAR ");
        sb.Append(my);
        if (!string.IsNullOrWhiteSpace(h.Ur))
            sb.Append(" → ").Append(h.Ur);
        if (!string.IsNullOrWhiteSpace(h.Rpt1))
            sb.Append(" via ").Append(h.Rpt1);
        if (!string.IsNullOrWhiteSpace(h.Rpt2) && !string.Equals(h.Rpt2, h.Rpt1, StringComparison.Ordinal))
            sb.Append('/').Append(h.Rpt2);
        if ((h.Flag1 & 0x20) != 0)
            sb.Append(" [EMR]");
        return sb.ToString();
    }

    private static int[] Convolve(ReadOnlySpan<int> info)
    {
        var output = new int[info.Length * 2];
        int s1 = 0, s2 = 0, at = 0;
        foreach (int b in info)
        {
            output[at++] = b ^ s1 ^ s2;
            output[at++] = b ^ s2;
            s2 = s1;
            s1 = b;
        }

        return output;
    }

    private static int[] Viterbi(ReadOnlySpan<int> coded)
    {
        const int states = 4;
        int pairs = coded.Length / 2;
        var metric = new int[states];
        var nextMetric = new int[states];
        var prev = new int[pairs, states];
        var bitChoice = new int[pairs, states];
        for (int s = 1; s < states; s++)
            metric[s] = 1_000_000;

        for (int t = 0; t < pairs; t++)
        {
            int c0 = coded[t * 2];
            int c1 = coded[t * 2 + 1];
            for (int s = 0; s < states; s++)
                nextMetric[s] = 1_000_000;

            for (int s = 0; s < states; s++)
            {
                int s1 = (s >> 1) & 1;
                int s2 = s & 1;
                for (int b = 0; b < 2; b++)
                {
                    int e0 = b ^ s1 ^ s2;
                    int e1 = b ^ s2;
                    int cost = metric[s] + (e0 == c0 ? 0 : 1) + (e1 == c1 ? 0 : 1);
                    int ns = (b << 1) | s1;
                    if (cost < nextMetric[ns])
                    {
                        nextMetric[ns] = cost;
                        prev[t, ns] = s;
                        bitChoice[t, ns] = b;
                    }
                }
            }

            Array.Copy(nextMetric, metric, states);
        }

        int best = 0;
        for (int s = 1; s < states; s++)
        {
            if (metric[s] < metric[best])
                best = s;
        }

        var bits = new int[pairs];
        int state = best;
        for (int t = pairs - 1; t >= 0; t--)
        {
            bits[t] = bitChoice[t, state];
            state = prev[t, state];
        }

        return bits;
    }

    private static int[] Interleave(ReadOnlySpan<int> bits)
    {
        var table = new int[12, 55];
        int at = 0;
        for (int r = 0; r < 12; r++)
        {
            for (int c = 0; c < 55; c++)
                table[r, c] = bits[at++];
        }

        var output = new int[660];
        at = 0;
        for (int c = 0; c < 55; c++)
        {
            for (int r = 0; r < 12; r++)
                output[at++] = table[r, c];
        }

        return output;
    }

    private static int[] Deinterleave(ReadOnlySpan<int> bits)
    {
        var table = new int[12, 55];
        int at = 0;
        for (int c = 0; c < 55; c++)
        {
            for (int r = 0; r < 12; r++)
                table[r, c] = bits[at++];
        }

        var output = new int[660];
        at = 0;
        for (int r = 0; r < 12; r++)
        {
            for (int c = 0; c < 55; c++)
                output[at++] = table[r, c];
        }

        return output;
    }

    private static int[] Scramble(ReadOnlySpan<int> bits)
    {
        var output = new int[bits.Length];
        int reg = 0x7F;
        for (int i = 0; i < bits.Length; i++)
        {
            int pn = (reg >> 6) & 1;
            int fb = ((reg >> 6) ^ (reg >> 3)) & 1;
            reg = ((reg << 1) | fb) & 0x7F;
            output[i] = bits[i] ^ pn;
        }

        return output;
    }

    private static ushort CrcX25(ReadOnlySpan<byte> data)
    {
        int crc = 0xFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0x8408 : crc >> 1;
        }

        return (ushort)(crc ^ 0xFFFF);
    }

    private static void WriteCall(Span<byte> dest, string text)
    {
        string padded = (text ?? "").Trim().ToUpperInvariant().PadRight(dest.Length)[..dest.Length];
        for (int i = 0; i < dest.Length; i++)
            dest[i] = (byte)padded[i];
    }

    private static string ReadCall(ReadOnlySpan<byte> field)
    {
        var chars = new char[field.Length];
        for (int i = 0; i < field.Length; i++)
        {
            char c = (char)field[i];
            chars[i] = c >= 32 && c <= 126 ? c : ' ';
        }

        return new string(chars).Trim();
    }

    private static byte[] BitsToBytes(ReadOnlySpan<int> bits)
    {
        int n = bits.Length / 8;
        var bytes = new byte[n];
        for (int i = 0; i < n; i++)
        {
            int v = 0;
            for (int b = 0; b < 8; b++)
                v = (v << 1) | (bits[i * 8 + b] & 1);
            bytes[i] = (byte)v;
        }

        return bytes;
    }

    private static byte[] BitsToBytes3(ReadOnlySpan<int> bits24)
    {
        var bytes = new byte[3];
        for (int i = 0; i < 3; i++)
        {
            int v = 0;
            for (int b = 0; b < 8; b++)
                v = (v << 1) | (bits24[i * 8 + b] & 1);
            bytes[i] = (byte)v;
        }

        return bytes;
    }

    private static byte[] UnscrambleSlow(byte[] three) =>
        [(byte)(three[0] ^ 0x70), (byte)(three[1] ^ 0x4F), (byte)(three[2] ^ 0x93)];

    private static bool IsDataSync(ReadOnlySpan<int> bits24)
    {
        int err = 0;
        for (int i = 0; i < 24; i++)
        {
            if (bits24[i] != DstarSync.DataSync[i] && ++err > 1)
                return false;
        }

        return true;
    }

    private static int[] EndPattern()
    {
        var end = new int[48];
        for (int i = 0; i < 32; i++)
            end[i] = (i & 1) == 0 ? 1 : 0;
        int[] tail = [0, 0, 0, 1, 0, 0, 1, 1, 0, 1, 0, 1, 1, 1, 1, 0];
        Array.Copy(tail, 0, end, 32, 16);
        return end;
    }

    private static char Printable(char c) => c >= 32 && c <= 126 ? c : ' ';

    private readonly record struct DstarHeader(byte Flag1, string Rpt2, string Rpt1, string Ur, string My, string Suffix);
}
