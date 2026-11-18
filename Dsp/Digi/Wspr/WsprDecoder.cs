using System.Numerics;
using System.Text;
using MathNet.Numerics.IntegralTransforms;

namespace AgenticSdr.Dsp;

/// <summary>One decoded WSPR Type-1 beacon (call, 4-char grid, power dBm).</summary>
public sealed record WsprMessage(string Text, double AudioHz, double SnrDb, double DtSeconds = 0);

/// <summary>
/// WSPR Type-1 receive/encode. 4-FSK at 1.4648 baud (~6 Hz), 162 symbols over ~110.6 s in a 120 s UTC slot.
/// Dial is USB; the tone stack sits near 1500 Hz above the dial (1400–1600 Hz window).
/// </summary>
public static class WsprDecoder
{
    public const int SampleRate = WsprTables.SampleRate;
    public const double CenterHz = WsprTables.CenterHz;
    public const double BandwidthHz = WsprTables.BandwidthHz;
    public const int SlotSeconds = WsprTables.SlotSeconds;

    private const int MaxStackPops = 200_000;
    private const double MinSyncMetric = 40;

    public static string Format(IReadOnlyList<WsprMessage> messages)
    {
        if (messages.Count == 0)
            return "";
        var lines = new List<string>
        {
            "WSPR  (SNR ≈ sync strength; Hz = lowest tone; text is CALL GRID dBm)"
        };
        foreach (WsprMessage message in messages)
        {
            lines.Add($"{message.SnrDb,4:0} dB  {message.AudioHz,6:0} Hz  {message.Text}");
            lines.Add("           WSPR Type-1 beacon (propagation report).");
        }

        return string.Join("\n", lines);
    }

    public static IReadOnlyList<WsprMessage> Decode(ReadOnlySpan<float> audio, int sampleRate)
    {
        if (audio.Length < sampleRate * 30 || sampleRate < 4_000)
            return [];

        float[] wave = Resample(audio, sampleRate, SampleRate);
        // Need most of the 110.6 s signal; allow slightly short clips from live chops.
        int need = (int)(WsprTables.SignalSeconds * SampleRate * 0.85);
        if (wave.Length < need)
            return [];

        if (!TryFindSync(wave, out int t0, out int bin0, out double metric))
            return [];
        if (metric < MinSyncMetric)
            return [];

        double[] soft = SoftSymbols(wave, t0, bin0);
        double[] llr = Deinterleave(soft);
        if (!TrySequentialDecode(llr, out byte[] bits))
            return [];
        if (!TryUnpack(bits, out string text))
            return [];

        double audioHz = bin0 * WsprTables.ToneSpacingHz;
        // Map sync metric (~40–162) into a rough dB-like score for the UI.
        double snrDb = Math.Clamp(metric * 0.2 - 20, -35, 30);
        return [new WsprMessage(text, audioHz, snrDb, t0 / (double)SampleRate)];
    }

    /// <summary>Synthesize one WSPR Type-1 transmission (signal only, no 120 s pad) for tests.</summary>
    public static float[] Modulate(string callsign, string grid4, int powerDbm, int sampleRate = SampleRate, double centerHz = CenterHz)
    {
        byte[] symbols = ChannelSymbols(callsign, grid4, powerDbm);
        int sps = Math.Max(8, (int)Math.Round(sampleRate * WsprTables.SamplesPerSymbol / (double)SampleRate));
        var wave = new float[symbols.Length * sps];
        double phase = 0;
        for (int s = 0; s < symbols.Length; s++)
        {
            double freq = centerHz + symbols[s] * WsprTables.ToneSpacingHz;
            double step = 2 * Math.PI * freq / sampleRate;
            for (int n = 0; n < sps; n++)
            {
                wave[s * sps + n] = 0.45f * (float)Math.Sin(phase);
                phase += step;
                if (phase > Math.PI)
                    phase -= 2 * Math.PI;
            }
        }

        return wave;
    }

    /// <summary>Full 120 s slot: 1 s silence + signal + pad (matches on-air framing).</summary>
    public static float[] ModulateSlot(string callsign, string grid4, int powerDbm, int sampleRate = SampleRate, double centerHz = CenterHz)
    {
        float[] signal = Modulate(callsign, grid4, powerDbm, sampleRate, centerHz);
        int total = sampleRate * SlotSeconds;
        var slot = new float[total];
        int lead = sampleRate; // 1 s
        Array.Copy(signal, 0, slot, lead, Math.Min(signal.Length, total - lead));
        return slot;
    }

    public static byte[] ChannelSymbols(string callsign, string grid4, int powerDbm)
    {
        byte[] bits = MessageBits(callsign, grid4, powerDbm);
        byte[] coded = Convolve(bits);
        byte[] interleaved = Interleave(coded);
        var symbols = new byte[WsprTables.SymbolCount];
        for (int i = 0; i < symbols.Length; i++)
            symbols[i] = (byte)(WsprTables.Sync[i] + 2 * interleaved[i]);
        return symbols;
    }

    public static uint PackCall(string callsign)
    {
        string c = NormalizeCall(callsign);
        uint n = Cv(c[0]);
        n = n * 36 + Cv(c[1]);
        n = n * 10 + Cv(c[2]);
        n = n * 27 + (Cv(c[3]) - 10);
        n = n * 27 + (Cv(c[4]) - 10);
        n = n * 27 + (Cv(c[5]) - 10);
        return n & 0x0FFFFFFFu;
    }

    public static uint PackGridPower(string grid4, int powerDbm)
    {
        string g = grid4.Trim().ToUpperInvariant();
        if (g.Length != 4)
            throw new ArgumentException("WSPR grid must be 4 characters.", nameof(grid4));
        int m1 = (179 - 10 * (g[0] - 'A') - (g[2] - '0')) * 180 + 10 * (g[1] - 'A') + (g[3] - '0');
        uint m = (uint)(m1 * 128 + powerDbm + 64);
        return m & 0x3FFFFFu;
    }

    private static byte[] MessageBits(string callsign, string grid4, int powerDbm)
    {
        uint n = PackCall(callsign);
        uint m = PackGridPower(grid4, powerDbm);
        var bits = new byte[WsprTables.MessageBits];
        for (int i = 0; i < 28; i++)
            bits[i] = (byte)((n >> (27 - i)) & 1);
        for (int i = 0; i < 22; i++)
            bits[28 + i] = (byte)((m >> (21 - i)) & 1);
        return bits;
    }

    private static string NormalizeCall(string callsign)
    {
        string c = callsign.Trim().ToUpperInvariant();
        if (c.Length >= 2 && !char.IsDigit(c[2]) && char.IsDigit(c[1]))
            c = " " + c;
        if (c.Length > 6)
            c = c[..6];
        c = c.PadRight(6);
        if (c.Length < 3 || !char.IsDigit(c[2]))
            throw new ArgumentException($"Call '{callsign}' is not a Type-1 WSPR callsign.", nameof(callsign));
        return c;
    }

    private static uint Cv(char ch) => ch switch
    {
        ' ' => 36,
        >= '0' and <= '9' => (uint)(ch - '0'),
        >= 'A' and <= 'Z' => (uint)(ch - 'A' + 10),
        _ => throw new ArgumentException($"Illegal WSPR character '{ch}'.")
    };

    private static byte[] Convolve(byte[] messageBits)
    {
        var output = new byte[(WsprTables.MessageBits + WsprTables.TailBits) * 2];
        uint reg = 0;
        int at = 0;
        for (int i = 0; i < WsprTables.MessageBits + WsprTables.TailBits; i++)
        {
            int bit = i < messageBits.Length ? messageBits[i] : 0;
            reg = (reg << 1) | (uint)bit;
            output[at++] = (byte)WsprTables.Parity32(reg & WsprTables.Poly0);
            output[at++] = (byte)WsprTables.Parity32(reg & WsprTables.Poly1);
        }

        return output;
    }

    private static byte[] Interleave(byte[] coded)
    {
        var d = new byte[WsprTables.SymbolCount];
        int p = 0;
        for (int i = 0; i < 256 && p < WsprTables.SymbolCount; i++)
        {
            int j = WsprTables.BitRev8(i);
            if (j < WsprTables.SymbolCount)
                d[j] = coded[p++];
        }

        return d;
    }

    private static double[] Deinterleave(double[] soft)
    {
        var output = new double[WsprTables.SymbolCount];
        int p = 0;
        for (int i = 0; i < 256 && p < WsprTables.SymbolCount; i++)
        {
            int j = WsprTables.BitRev8(i);
            if (j < WsprTables.SymbolCount)
                output[p++] = soft[j];
        }

        return output;
    }

    private static bool TryFindSync(float[] wave, out int t0, out int bin0, out double metric)
    {
        t0 = 0;
        bin0 = 0;
        metric = double.NegativeInfinity;
        int binLo = (int)(1_400 / WsprTables.ToneSpacingHz);
        int binHi = (int)(1_600 / WsprTables.ToneSpacingHz);
        int nominalBin = (int)Math.Round(CenterHz / WsprTables.ToneSpacingHz);

        // Fast path: nominal 1 s start + bins near 1500 Hz (and a few nearby starts).
        var tried = new HashSet<int>();
        int[] starts = [SampleRate, 0, SampleRate / 2, SampleRate + SampleRate / 2, 2 * SampleRate];
        foreach (int start in starts)
        {
            if (!tried.Add(start))
                continue;
            if (start + WsprTables.SymbolCount * WsprTables.SamplesPerSymbol / 2 > wave.Length)
                continue;
            double[,] powers = TonePowerMatrix(wave, start, binLo, binHi + 3);
            for (int b = Math.Max(binLo, nominalBin - 40); b <= Math.Min(binHi, nominalBin + 40); b++)
            {
                double score = SyncMetric(powers, binLo, b);
                if (score > metric)
                {
                    metric = score;
                    t0 = start;
                    bin0 = b;
                }
            }

            if (metric >= MinSyncMetric * 2)
                break;
        }

        // Broader time/frequency hunt when the fast path is soft (real on-air mistunes).
        if (metric < MinSyncMetric * 1.5)
        {
            for (int start = 0; start < 30_000; start += 4_096)
            {
                if (start + WsprTables.SymbolCount * WsprTables.SamplesPerSymbol / 2 > wave.Length)
                    break;
                if (!tried.Add(start))
                    continue;
                double[,] powers = TonePowerMatrix(wave, start, binLo, binHi + 3);
                for (int b = binLo; b <= binHi; b++)
                {
                    double score = SyncMetric(powers, binLo, b);
                    if (score > metric)
                    {
                        metric = score;
                        t0 = start;
                        bin0 = b;
                    }
                }
            }
        }

        return double.IsFinite(metric) && metric > 0;
    }

    /// <summary>162 × (binHi-binLo+1) power matrix from one rectangular FFT per symbol.</summary>
    private static double[,] TonePowerMatrix(float[] wave, int t0, int binLo, int binHi)
    {
        int width = binHi - binLo + 1;
        var wide = new double[WsprTables.SymbolCount, width];
        var frame = new Complex[WsprTables.SamplesPerSymbol];
        for (int n = 0; n < WsprTables.SymbolCount; n++)
        {
            int start = t0 + n * WsprTables.SamplesPerSymbol;
            for (int i = 0; i < frame.Length; i++)
            {
                int at = start + i;
                frame[i] = (uint)at < (uint)wave.Length ? wave[at] : 0;
            }

            Fourier.Forward(frame, FourierOptions.NoScaling);
            for (int b = 0; b < width; b++)
            {
                Complex c = frame[binLo + b];
                wide[n, b] = c.Real * c.Real + c.Imaginary * c.Imaginary;
            }
        }

        return wide;
    }

    private static double SyncMetric(double[,] wide, int binLo, int bin0)
    {
        int local = bin0 - binLo;
        if (local < 0 || local + 3 >= wide.GetLength(1))
            return double.NegativeInfinity;

        double metric = 0;
        for (int n = 0; n < WsprTables.SymbolCount; n++)
        {
            double p0 = wide[n, local];
            double p1 = wide[n, local + 1];
            double p2 = wide[n, local + 2];
            double p3 = wide[n, local + 3];
            double tot = p0 + p1 + p2 + p3 + 1e-12;
            double good = WsprTables.Sync[n] == 0 ? p0 + p2 : p1 + p3;
            double bad = WsprTables.Sync[n] == 0 ? p1 + p3 : p0 + p2;
            metric += (good - bad) / tot;
        }

        return metric;
    }

    private static double[] SoftSymbols(float[] wave, int t0, int bin0)
    {
        int binLo = (int)(1_400 / WsprTables.ToneSpacingHz);
        int binHi = (int)(1_600 / WsprTables.ToneSpacingHz) + 3;
        double[,] wide = TonePowerMatrix(wave, t0, binLo, binHi);
        int local = bin0 - binLo;
        var soft = new double[WsprTables.SymbolCount];
        for (int n = 0; n < WsprTables.SymbolCount; n++)
        {
            double p0 = wide[n, local];
            double p1 = wide[n, local + 1];
            double p2 = wide[n, local + 2];
            double p3 = wide[n, local + 3];
            double d0 = WsprTables.Sync[n] == 0 ? p0 : p1;
            double d1 = WsprTables.Sync[n] == 0 ? p2 : p3;
            soft[n] = d1 - d0;
        }

        return soft;
    }

    private static bool TrySequentialDecode(double[] convLlr, out byte[] messageBits)
    {
        messageBits = [];
        double mean = 0;
        for (int i = 0; i < convLlr.Length; i++)
            mean += Math.Abs(convLlr[i]);
        mean /= Math.Max(1, convLlr.Length);
        if (mean < 1e-18)
            return false;

        var r = new double[convLlr.Length];
        for (int i = 0; i < convLlr.Length; i++)
            r[i] = convLlr[i] / mean;

        // Max-heap via PriorityQueue with negated metric.
        var heap = new PriorityQueue<(double Metric, int Depth, uint Reg, byte[] Bits), double>();
        heap.Enqueue((0, 0, 0, []), 0);
        int pops = 0;
        const double alpha = 2.0;
        int depthTarget = WsprTables.MessageBits + WsprTables.TailBits;

        while (heap.Count > 0 && pops < MaxStackPops)
        {
            (double metric, int depth, uint reg, byte[] bits) = heap.Dequeue();
            pops++;
            if (depth == depthTarget)
            {
                messageBits = bits.AsSpan(0, WsprTables.MessageBits).ToArray();
                return true;
            }

            // Tail bits are known zeros after the 50 information bits.
            int bLo = depth >= WsprTables.MessageBits ? 0 : 0;
            int bHi = depth >= WsprTables.MessageBits ? 0 : 1;
            for (int b = bLo; b <= bHi; b++)
            {
                uint reg2 = (reg << 1) | (uint)b;
                int e0 = WsprTables.Parity32(reg2 & WsprTables.Poly0);
                int e1 = WsprTables.Parity32(reg2 & WsprTables.Poly1);
                double bm = BitMetric(r[2 * depth], e0, alpha) + BitMetric(r[2 * depth + 1], e1, alpha);
                var nextBits = new byte[bits.Length + 1];
                bits.CopyTo(nextBits, 0);
                nextBits[^1] = (byte)b;
                double nextMetric = metric + bm;
                heap.Enqueue((nextMetric, depth + 1, reg2, nextBits), -nextMetric);
            }
        }

        return false;
    }

    private static double BitMetric(double val, int expected, double alpha)
    {
        double x = alpha * val * (expected == 1 ? 1 : -1);
        // 0.5 + log2(σ(x)), σ = 1/(1+e^-x)
        double log2Sigma;
        if (x > 30)
            log2Sigma = 0;
        else if (x < -30)
            log2Sigma = x / Math.Log(2);
        else
            log2Sigma = -Math.Log(1 + Math.Exp(-x)) / Math.Log(2);
        return 0.5 + log2Sigma;
    }

    private static bool TryUnpack(byte[] bits, out string text)
    {
        text = "";
        if (bits.Length < WsprTables.MessageBits)
            return false;

        uint n = 0;
        for (int i = 0; i < 28; i++)
            n = (n << 1) | bits[i];
        uint m = 0;
        for (int i = 0; i < 22; i++)
            m = (m << 1) | bits[28 + i];

        char c6 = Ch((int)(n % 27) + 10); n /= 27;
        char c5 = Ch((int)(n % 27) + 10); n /= 27;
        char c4 = Ch((int)(n % 27) + 10); n /= 27;
        char c3 = Ch((int)(n % 10)); n /= 10;
        char c2 = Ch((int)(n % 36)); n /= 36;
        char c1 = Ch((int)n);
        string call = new string([c1, c2, c3, c4, c5, c6]).Trim();
        if (call.Length == 0)
            return false;

        int pwr = (int)(m % 128) - 64;
        int m1 = (int)(m / 128);
        int lo = m1 % 180;
        int l2 = lo / 10;
        int l4 = lo % 10;
        int v = 179 - m1 / 180;
        int l1 = v / 10;
        int l3 = v % 10;
        if (l1 is < 0 or >= 18 || l2 is < 0 or >= 18 || pwr is < 0 or > 60)
            return false;

        var grid = new StringBuilder(4);
        grid.Append((char)('A' + l1));
        grid.Append((char)('A' + l2));
        grid.Append((char)('0' + l3));
        grid.Append((char)('0' + l4));
        text = $"{call} {grid} {pwr}";
        return true;
    }

    private static char Ch(int v) => v switch
    {
        < 10 => (char)('0' + v),
        < 36 => (char)('A' + v - 10),
        _ => ' '
    };

    private static float[] Resample(ReadOnlySpan<float> audio, int fromRate, int toRate)
    {
        if (fromRate == toRate)
            return audio.ToArray();
        double step = fromRate / (double)toRate;
        int count = Math.Max(1, (int)(audio.Length / step));
        var output = new float[count];
        for (int i = 0; i < count; i++)
        {
            double at = i * step;
            int lo = (int)at;
            int hi = Math.Min(audio.Length - 1, lo + 1);
            double frac = at - lo;
            output[i] = (float)(audio[lo] * (1 - frac) + audio[hi] * frac);
        }

        return output;
    }
}
