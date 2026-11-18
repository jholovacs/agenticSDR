using System.Text;

namespace AgenticSdr.Dsp;

/// <summary>One decoded Olivia character run with audio pitch, strength, and mode profile.</summary>
public sealed record OliviaMessage(string Text, double AudioHz, double SnrDb, string Profile);

/// <summary>One Olivia T/BW combination (tone count and occupied bandwidth).</summary>
public readonly record struct OliviaProfile(int Tones, double BandwidthHz, string Name)
{
    public int BitsPerSymbol => (int)Math.Log2(Tones);
}

/// <summary>
/// Olivia MFSK keyboard chat. HF calling frequencies use 8/250; QSOs often move to 8/500 or 16/500.
/// Decode follows the public Walsh/Hadamard MFSK block description used by fldigi-family Olivia.
/// Audio is measured above the USB dial (same window as FT8/JS8), with the tone stack centered near 1500 Hz.
/// </summary>
public static class OliviaDecoder
{
    public const int SampleRate = 8_000;
    /// <summary>
    /// Published calling channels use dial + 1500 Hz as the tone-stack center (fldigi / Olivia group).
    /// Decode still hunts 1000 and 1250 Hz cursors used by some operators.
    /// </summary>
    public const double CenterHz = 1_500;
    /// <summary>Calling-frequency bandwidth used for occupancy scoring and UI width.</summary>
    public const double BandwidthHz = 250;
    public const int Tones = 8;

    private const int BlockSymbols = 64;
    private const ulong Scrambler = 0xE257E6D0291574ECUL;
    private const int ScrambleShift = 13;
    /// <summary>
    /// Walsh peak²/noise mean. Clean self-tests sit well above 20; usable on-air HF often lands ~10–18.
    /// LooksLikeChat still rejects noise Walsh peaks.
    /// </summary>
    private const double MinConfidence = 10.0;
    /// <summary>Common waterfall cursors used for Olivia above the USB dial (calling = 1500 first).</summary>
    private static readonly double[] NominalCenters = [1_500, 1_000, 1_250];

    /// <summary>Profiles tried in order; first is the HF calling convention.</summary>
    public static readonly OliviaProfile[] Profiles =
    [
        new(8, 250, "8/250"),
        new(8, 500, "8/500"),
        new(16, 500, "16/500"),
        new(16, 1_000, "16/1000"),
        new(32, 1_000, "32/1000")
    ];

    public static string Format(IReadOnlyList<OliviaMessage> messages)
    {
        if (messages.Count == 0)
            return "";
        string profile = messages[0].Profile;
        var lines = new List<string>
        {
            $"Olivia {profile}  (SNR = strength; Hz = tone center in the audio; text is keyboard chat)"
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
        SoftAgc(wave);
        if (!HasToneEnergy(wave))
            return [];

        // Hunt the tone stack across the usual USB cursor range (1000–1500) plus dongle residual.
        var centers = new List<double>();
        var seen = new HashSet<int>();
        void Offer(double hz)
        {
            int key = (int)Math.Round(hz / 12.5); // 12.5 Hz buckets (half of 8/250 tone spacing)
            if (hz < 600 || hz > 2_400 || !seen.Add(key))
                return;
            centers.Add(hz);
        }

        foreach (double nominal in NominalCenters)
        {
            double offset = DialFrequency.EstimateBandOffsetHz(
                wave, SampleRate, nominal, BandwidthHz * 0.5, DialFrequency.DefaultSearchHz + 150, stepHz: 12.5);
            foreach (double c in DialFrequency.CenterCandidates(nominal, offset, polishHz: 100, polishStepHz: 12.5))
                Offer(c);
        }

        double wide = DialFrequency.EstimateBandOffsetHz(
            wave, SampleRate, CenterHz, 500, DialFrequency.DefaultSearchHz + 250, stepHz: 12.5);
        Offer(CenterHz + wide);

        OliviaMessage? best = null;
        double bestConf = 0;
        void Consider(OliviaProfile profile, IEnumerable<double> tryCenters)
        {
            foreach (double center in tryCenters)
            {
                if (TryDecode(wave, profile, center, out OliviaMessage message, out double conf)
                    && conf > bestConf)
                {
                    bestConf = conf;
                    best = message;
                }
            }
        }

        // Calling 8/250 first — most HF watering-hole traffic.
        Consider(Profiles[0], centers);
        if (best is null || bestConf < MinConfidence * 1.5)
        {
            for (int i = 1; i < Profiles.Length; i++)
            {
                Consider(Profiles[i], centers);
                if (best is not null && bestConf >= MinConfidence * 1.5)
                    break;
            }
        }

        return best is null ? [] : [best];
    }

    /// <summary>
    /// Synthesize Olivia audio for tests. Characters are packed <see cref="OliviaProfile.BitsPerSymbol"/> per block.
    /// </summary>
    public static float[] Modulate(string text, OliviaProfile profile, int sampleRate, double centerHz = CenterHz)
    {
        int tones = profile.Tones;
        int bps = profile.BitsPerSymbol;
        double spacing = profile.BandwidthHz / tones;
        double baud = spacing;
        int sps = Math.Max(8, (int)Math.Round(sampleRate / baud));
        double baseHz = centerHz - profile.BandwidthHz * (1.0 - 0.5 / tones) / 2.0;

        var chars = new List<int>();
        foreach (char ch in text)
            chars.Add(ch & 0x7F);
        while (chars.Count % bps != 0)
            chars.Add(0);

        var symbols = new List<int>();
        // Idle lead-in so a receiver can lock block phase.
        for (int i = 0; i < bps; i++)
            symbols.AddRange(EncodeBlock(Enumerable.Repeat(0, bps).ToArray(), bps));
        for (int i = 0; i < chars.Count; i += bps)
            symbols.AddRange(EncodeBlock(chars.GetRange(i, bps).ToArray(), bps));
        for (int i = 0; i < bps; i++)
            symbols.AddRange(EncodeBlock(Enumerable.Repeat(0, bps).ToArray(), bps));

        var wave = new float[symbols.Count * sps];
        double phase = 0;
        for (int s = 0; s < symbols.Count; s++)
        {
            int tone = Gray(symbols[s]);
            double freq = baseHz + tone * spacing;
            double step = 2 * Math.PI * freq / sampleRate;
            for (int n = 0; n < sps; n++)
            {
                wave[s * sps + n] = 0.5f * (float)Math.Cos(phase);
                phase += step;
                if (phase > Math.PI)
                    phase -= 2 * Math.PI;
            }
        }

        return wave;
    }

    private static bool HasToneEnergy(float[] wave)
    {
        int n = Math.Min(wave.Length, SampleRate);
        if (n < 64)
            return false;
        double sum = 0;
        for (int i = 0; i < n; i++)
            sum += wave[i] * wave[i];
        return sum / n > 1e-8;
    }

    private static bool TryDecode(
        float[] wave,
        OliviaProfile profile,
        double centerHz,
        out OliviaMessage message,
        out double confidence)
    {
        message = null!;
        confidence = 0;
        int tones = profile.Tones;
        int bitsPerSymbol = profile.BitsPerSymbol;
        if (tones < 2 || (tones & (tones - 1)) != 0 || bitsPerSymbol < 1)
            return false;

        double spacing = profile.BandwidthHz / tones;
        double baud = spacing;
        int sps = Math.Max(8, (int)Math.Round(SampleRate / baud));
        // fldigi: FirstCarrier = center − Bandwidth·(1 − 0.5/Tones)/2, then + t·spacing.
        double baseHz = centerHz - profile.BandwidthHz * (1.0 - 0.5 / tones) / 2.0;
        if (baseHz < 40 || baseHz + (tones - 1) * spacing > SampleRate * 0.48)
            return false;

        string bestText = "";
        double bestConf = 0;
        double bestSnr = 0;
        // sps/16 (~2 ms at 8/250) catches weak locks the coarser sps/8 grid misses.
        int timingStep = Math.Max(1, sps / 16);
        for (int timing = 0; timing < sps; timing += timingStep)
        {
            var softBlocks = new List<double[]>();
            var snrs = new List<double>();
            for (int start = timing; start + sps <= wave.Length; start += sps)
            {
                var mags = new double[tones];
                for (int t = 0; t < tones; t++)
                    mags[t] = Goertzel(wave.AsSpan(start, sps), SampleRate, baseHz + t * spacing);

                var ordered = (double[])mags.Clone();
                Array.Sort(ordered);
                double noise = Math.Max(1e-12, ordered[Math.Max(0, tones / 4)]);
                double peak = ordered[^1];
                snrs.Add(10 * Math.Log10(Math.Max(peak, 1e-18) / noise));
                softBlocks.Add(SoftBits(mags, bitsPerSymbol, tones));
            }

            if (softBlocks.Count < BlockSymbols)
                continue;

            string text = DecodeStream(softBlocks, bitsPerSymbol, out double conf);
            if (conf > bestConf)
            {
                bestConf = conf;
                bestText = text;
                bestSnr = snrs.Count == 0 ? 0 : snrs.Average();
            }
        }

        if (string.IsNullOrWhiteSpace(bestText) || bestConf < MinConfidence)
            return false;
        if (!LooksLikeChat(bestText))
            return false;

        confidence = bestConf;
        message = new OliviaMessage(Clean(bestText), centerHz, Math.Clamp(bestSnr, -20, 40), profile.Name);
        return true;
    }

    /// <summary>Raise quiet HF digi audio to a stable RMS so Goertzel magnitudes stay comparable.</summary>
    private static void SoftAgc(float[] wave)
    {
        double sum = 0;
        for (int i = 0; i < wave.Length; i++)
            sum += wave[i] * wave[i];
        double rms = Math.Sqrt(sum / Math.Max(1, wave.Length));
        // Only lift truly quiet HF digi; leave normal/noisy clips alone so noise does not get “boosted” into false Walsh peaks.
        if (rms < 1e-9 || rms > 0.05)
            return;
        float gain = (float)(0.12 / rms);
        for (int i = 0; i < wave.Length; i++)
            wave[i] *= gain;
    }

    /// <summary>Reject Walsh peaks that only invent punctuation soup from noise.</summary>
    private static bool LooksLikeChat(string text)
    {
        text = text.Trim();
        int letters = 0;
        int printable = 0;
        int weird = 0;
        int spaces = 0;
        foreach (char ch in text)
        {
            if (ch is >= ' ' and <= '~')
                printable++;
            if (ch == ' ')
                spaces++;
            if (char.IsLetterOrDigit(ch) || ch is ' ' or '.' or ',' or '?' or '!' or '/' or '-')
                letters++;
            else if (ch is >= ' ' and <= '~')
                weird++;
        }

        if (printable < 4)
            return false;
        // Real keyboard chat is mostly letters/spaces; any bracket/symbol soup is noise.
        if (letters < printable * 0.75 || weird > 0)
            return false;

        int upper = 0, lower = 0, digits = 0;
        foreach (char ch in text)
        {
            if (char.IsUpper(ch)) upper++;
            else if (char.IsLower(ch)) lower++;
            else if (char.IsDigit(ch)) digits++;
        }

        // On-air Olivia is almost always typed uppercase; lowercase/digit soup is Walsh noise.
        if (lower > 0 || upper < 3)
            return false;
        if (digits > upper)
            return false;

        // Short no-space blobs: allow a single UPPERCASE word (HELLO, callsign) only.
        if (spaces == 0)
        {
            if (printable is < 4 or > 10)
                return false;
            foreach (char ch in text)
            {
                if (!char.IsLetterOrDigit(ch))
                    return false;
            }
        }
        else if (printable < 6)
            return false;
        // Reject highly repetitive Walsh garbage (e.g. GZZGZZGZZ…).
        if (printable >= 8 && DistinctRatio(text) < 0.35)
            return false;
        return true;
    }

    private static double DistinctRatio(string text)
    {
        var set = new HashSet<char>();
        int n = 0;
        foreach (char ch in text)
        {
            if (ch == ' ')
                continue;
            set.Add(char.ToUpperInvariant(ch));
            n++;
        }

        return n == 0 ? 0 : set.Count / (double)n;
    }

    private static string DecodeStream(List<double[]> symbols, int bitsPerSymbol, out double bestConf)
    {
        bestConf = 0;
        string best = "";
        int search = Math.Min(BlockSymbols, symbols.Count - BlockSymbols + 1);
        for (int phase = 0; phase < search; phase++)
        {
            var chars = new StringBuilder();
            double confSum = 0;
            int blocks = 0;
            for (int at = phase; at + BlockSymbols <= symbols.Count; at += BlockSymbols)
            {
                string chunk = DecodeBlock(symbols.GetRange(at, BlockSymbols), bitsPerSymbol, out double conf);
                if (chunk.Length == 0)
                    continue;
                chars.Append(chunk);
                confSum += conf;
                blocks++;
            }

            if (blocks == 0)
                continue;
            double mean = confSum / blocks;
            if (mean > bestConf)
            {
                bestConf = mean;
                best = chars.ToString();
            }
        }

        return best;
    }

    private static int[] EncodeBlock(int[] chars, int bitsPerSymbol)
    {
        var output = new int[BlockSymbols];
        for (int freqbit = 0; freqbit < bitsPerSymbol; freqbit++)
        {
            int ch = chars[freqbit] & 0x7F;
            var fht = new double[BlockSymbols];
            if (ch < BlockSymbols)
                fht[ch] = 1;
            else
                fht[ch - BlockSymbols] = -1;
            InverseFht(fht);

            int codebit = (freqbit * ScrambleShift) & (BlockSymbols - 1);
            int rotate = 0;
            for (int tb = 0; tb < BlockSymbols; tb++)
            {
                double chip = fht[tb];
                if (((Scrambler >> codebit) & 1UL) != 0)
                    chip = -chip;
                if (chip < 0)
                    output[tb] |= 1 << ((freqbit + rotate) % bitsPerSymbol);
                codebit = (codebit + 1) & (BlockSymbols - 1);
                rotate = (rotate + 1) % bitsPerSymbol;
            }
        }

        return output;
    }

    private static string DecodeBlock(List<double[]> syms, int bitsPerSymbol, out double confidence)
    {
        confidence = 0;
        if (syms.Count != BlockSymbols)
            return "";

        var output = new StringBuilder();
        double confSum = 0;
        for (int freqbit = 0; freqbit < bitsPerSymbol; freqbit++)
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
                rotate = (rotate + 1) % bitsPerSymbol;
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
            // Surface printable keyboard text only; drop idle NUL and other controls.
            if (ch is >= 32 and < 127)
                output.Append((char)ch);
            else if (ch is 10 or 13)
                output.Append(' ');
        }

        confidence = confSum / bitsPerSymbol;
        return output.ToString();
    }

    private static double[] SoftBits(double[] mags, int bitsPerSymbol, int tones)
    {
        var soft = new double[bitsPerSymbol];
        for (int bit = 0; bit < bitsPerSymbol; bit++)
        {
            double sum = 0;
            for (int tone = 0; tone < tones; tone++)
            {
                int symbol = InvGray(tone, bitsPerSymbol);
                bool bitSet = ((symbol >> bit) & 1) != 0;
                sum += bitSet ? -mags[tone] : mags[tone];
            }

            soft[bit] = sum;
        }

        return soft;
    }

    private static int Gray(int value) => value ^ (value >> 1);

    private static int InvGray(int gray, int bitsPerSymbol)
    {
        int binary = gray;
        for (int shift = 1; shift < bitsPerSymbol; shift <<= 1)
            binary ^= binary >> shift;
        return binary & ((1 << bitsPerSymbol) - 1);
    }

    /// <summary>
    /// Jalocha / fldigi forward FHT (fine→coarse): (a,b) → (a+b, b−a).
    /// Must match InverseFht; the older (a+b, a−b) both-ways transform was self-consistent
    /// but could not decode on-air Olivia from fldigi-family transmitters.
    /// </summary>
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
                    data[i + j + step] = b - a;
                }
            }
        }
    }

    /// <summary>Jalocha / fldigi inverse FHT (coarse→fine): (a,b) → (a−b, a+b).</summary>
    private static void InverseFht(double[] data)
    {
        int n = data.Length;
        for (int step = n / 2; step > 0; step >>= 1)
        {
            for (int i = 0; i < n; i += step << 1)
            {
                for (int j = 0; j < step; j++)
                {
                    double a = data[i + j];
                    double b = data[i + j + step];
                    data[i + j] = a - b;
                    data[i + j + step] = a + b;
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
