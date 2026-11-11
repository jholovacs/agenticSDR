using System.Text;

namespace AgenticSdr.Dsp;

/// <summary>One decoded Olivia character run with audio pitch and strength.</summary>
public sealed record OliviaMessage(string Text, double AudioHz, double SnrDb);

/// <summary>
/// Olivia MFSK keyboard chat. Default profile is 8/500 (eight tones in 500 Hz), the common HF watering-hole mode.
/// Decode follows the public Walsh/Hadamard MFSK block description used by fldigi-family Olivia.
/// </summary>
public static class OliviaDecoder
{
    public const int SampleRate = 8_000;
    public const int Tones = 8;
    public const double BandwidthHz = 500;
    public const double CenterHz = 1_000;
    private const int BitsPerSymbol = 3;
    private const int BitsPerChar = 7;
    private const int BlockSymbols = 64;
    private const ulong Scrambler = 0xE257E6D0291574ECUL;
    private const int ScrambleShift = 13;

    public static string Format(IReadOnlyList<OliviaMessage> messages)
    {
        if (messages.Count == 0)
            return "";
        var lines = new List<string>
        {
            "Olivia 8/500  (SNR = strength; Hz = tone center in the audio; text is keyboard chat)"
        };
        foreach (OliviaMessage message in messages)
        {
            lines.Add($"{message.SnrDb,4:0} dB  {message.AudioHz,6:0} Hz  {message.Text}");
            lines.Add("           Olivia keyboard text (MFSK with Walsh FEC).");
        }

        return string.Join("\n", lines);
    }

    public static IReadOnlyList<OliviaMessage> Decode(ReadOnlySpan<float> audio, int sampleRate)
    {
        if (audio.Length < sampleRate || sampleRate < 4_000)
            return [];

        float[] wave = Resample(audio, sampleRate, SampleRate);
        double spacing = BandwidthHz / Tones;
        double baud = spacing;
        int sps = Math.Max(8, (int)Math.Round(SampleRate / baud));
        double baseHz = CenterHz - 0.5 * (Tones - 1) * spacing;

        var softBlocks = new List<double[]>();
        var snrs = new List<double>();
        int hops = Math.Max(1, sps / 4);
        for (int start = 0; start + sps <= wave.Length; start += hops)
        {
            var mags = new double[Tones];
            double noise = 0;
            for (int t = 0; t < Tones; t++)
            {
                double freq = baseHz + t * spacing;
                mags[t] = Goertzel(wave.AsSpan(start, sps), SampleRate, freq);
            }

            // Noise estimate from quiet bins: mean of the quieter half.
            var ordered = (double[])mags.Clone();
            Array.Sort(ordered);
            noise = Math.Max(1e-12, ordered[Tones / 4]);
            double peak = ordered[^1];
            snrs.Add(10 * Math.Log10(Math.Max(peak, 1e-18) / noise));

            softBlocks.Add(SoftBits(mags));
        }

        if (softBlocks.Count < BlockSymbols)
            return [];

        string text = DecodeStream(softBlocks, out double conf);
        if (string.IsNullOrWhiteSpace(text) || conf < 1.5)
            return [];

        double snr = snrs.Count == 0 ? 0 : snrs.Average();
        return [new OliviaMessage(Clean(text), CenterHz, Math.Clamp(snr, -20, 40))];
    }

    private static string DecodeStream(List<double[]> symbols, out double bestConf)
    {
        bestConf = 0;
        string best = "";
        int search = Math.Min(BlockSymbols, symbols.Count - BlockSymbols + 1);
        for (int phase = 0; phase < search; phase += Math.Max(1, BlockSymbols / 8))
        {
            var chars = new StringBuilder();
            double confSum = 0;
            int blocks = 0;
            for (int at = phase; at + BlockSymbols <= symbols.Count; at += BlockSymbols)
            {
                string chunk = DecodeBlock(symbols.GetRange(at, BlockSymbols), out double conf);
                if (chunk.Length == 0)
                    continue;
                chars.Append(chunk);
                confSum += conf;
                blocks++;
            }

            if (blocks == 0)
                continue;
            double mean = confSum / blocks;
            if (mean > bestConf && chars.Length > best.Length)
            {
                bestConf = mean;
                best = chars.ToString();
            }
        }

        return best;
    }

    private static string DecodeBlock(List<double[]> syms, out double confidence)
    {
        confidence = 0;
        if (syms.Count != BlockSymbols)
            return "";

        var output = new StringBuilder();
        double confSum = 0;
        for (int freqbit = 0; freqbit < BitsPerSymbol; freqbit++)
        {
            var fht = new double[BlockSymbols];
            int rotate = freqbit;
            int codebit = (freqbit * ScrambleShift) & (BlockSymbols - 1);
            for (int tb = 0; tb < BlockSymbols; tb++)
            {
                double x = syms[tb][rotate];
                if (((Scrambler >> codebit) & 1UL) != 0)
                    x = -x;
                fht[tb] = x;
                codebit = (codebit + 1) & (BlockSymbols - 1);
                rotate = (rotate + 1) % BitsPerSymbol;
            }

            ForwardFht(fht);
            int peakPos = 0;
            double peak = 0;
            double energy = 0;
            for (int i = 0; i < fht.Length; i++)
            {
                energy += fht[i] * fht[i];
                if (Math.Abs(fht[i]) > Math.Abs(peak))
                {
                    peak = fht[i];
                    peakPos = i;
                }
            }

            double noise = Math.Max(1e-12, (energy - peak * peak) / Math.Max(1, BlockSymbols - 1));
            confSum += (peak * peak) / noise;
            int ch = peakPos + (peak < 0 ? BlockSymbols : 0);
            if (ch is >= 32 and <= 126)
                output.Append((char)ch);
            else if (ch is >= 0 and < 32)
                output.Append(' ');
        }

        confidence = confSum / BitsPerSymbol;
        return output.ToString();
    }

    private static double[] SoftBits(double[] mags)
    {
        var soft = new double[BitsPerSymbol];
        for (int bit = 0; bit < BitsPerSymbol; bit++)
        {
            double sum = 0;
            for (int tone = 0; tone < Tones; tone++)
            {
                int symbol = InvGray(tone);
                bool bitSet = ((symbol >> bit) & 1) != 0;
                sum += bitSet ? -mags[tone] : mags[tone];
            }

            soft[bit] = sum;
        }

        return soft;
    }

    private static int InvGray(int gray)
    {
        int binary = gray;
        for (int shift = 1; shift < BitsPerSymbol; shift <<= 1)
            binary ^= binary >> shift;
        return binary & (Tones - 1);
    }

    private static void ForwardFht(double[] data)
    {
        int n = data.Length;
        for (int step = 1; step < n; step <<= 1)
        {
            for (int i = 0; i < n; i += step << 1)
            {
                for (int j = 0; j < step; j++)
                {
                    double a = data[i + j];
                    double b = data[i + j + step];
                    data[i + j] = a + b;
                    data[i + j + step] = a - b;
                }
            }
        }
    }

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

    private static double Goertzel(ReadOnlySpan<float> samples, int sampleRate, double frequencyHz)
    {
        double w = 2 * Math.PI * frequencyHz / sampleRate;
        double coeff = 2 * Math.Cos(w);
        double s0 = 0, s1 = 0, s2 = 0;
        foreach (float sample in samples)
        {
            s0 = sample + coeff * s1 - s2;
            s2 = s1;
            s1 = s0;
        }

        double real = s1 - s2 * Math.Cos(w);
        double imag = s2 * Math.Sin(w);
        return real * real + imag * imag;
    }

    private static string Clean(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char ch in text)
        {
            if (ch is >= ' ' and <= '~')
                sb.Append(ch);
            else if (char.IsWhiteSpace(ch))
                sb.Append(' ');
        }

        return sb.ToString().Trim();
    }
}
