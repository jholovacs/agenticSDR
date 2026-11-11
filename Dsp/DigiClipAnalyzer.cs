namespace AgenticSdr.Dsp;

/// <summary>One decoded line from a digi analysis pass.</summary>
public sealed record DigiAnalysisLine(string Mode, string Text, double AudioHz, double SnrDb);

/// <summary>
/// Plain-language digi diagnosis for a listen WAV: energy shape, Costas/LDPC counters, and whether
/// the decoder path can still read a synthetic frame mixed into the same noise.
/// </summary>
public sealed record DigiAnalysisReport(
    int SampleRate,
    double DurationSeconds,
    double Rms,
    double Peak,
    int HotBins200To3000,
    double? DominantPeakHz,
    double? DominantWidthHz,
    double? ToneEntropy,
    int Ft8Count,
    float Ft8BestSync,
    int Ft8LdpcOk,
    int Js8Count,
    bool VaracMonitorAvailable,
    string? VaracStatus,
    bool SelfTestOk,
    IReadOnlyList<DigiAnalysisLine> Messages,
    IReadOnlyList<string> Hints);

/// <summary>Runs the same digi checks used offline so the in-app AI can diagnose empty FT8/JS8/VarAC clips.</summary>
public static class DigiClipAnalyzer
{
    public static DigiAnalysisReport Analyze(ReadOnlySpan<float> audio, int sampleRate, string? modeHint = null)
    {
        if (audio.Length == 0 || sampleRate < 4_000)
        {
            return new DigiAnalysisReport(
                sampleRate, 0, 0, 0, 0, null, null, null,
                0, 0, 0, 0, false, null, false, [],
                ["Audio was empty or the sample rate was too low."]);
        }

        double rms = 0;
        float peak = 0;
        foreach (float sample in audio)
        {
            rms += (double)sample * sample;
            peak = Math.Max(peak, Math.Abs(sample));
        }

        rms = Math.Sqrt(rms / audio.Length);
        double duration = audio.Length / (double)sampleRate;

        SpectrumStats spectrum = MeasureSpectrum(audio, sampleRate);
        Ft8DecodeStats ft8 = Ft8Decoder.DecodeDetailed(audio, sampleRate);
        IReadOnlyList<Js8Message> js8 = Js8Decoder.Decode(audio, sampleRate);

        bool varacAvailable;
        string? varacStatus;
        using (var monitor = new XaraMonitor())
        {
            varacAvailable = monitor.Available;
            varacStatus = XaraMonitor.FormatAudio(audio, sampleRate);
        }

        var messages = new List<DigiAnalysisLine>();
        foreach (Ft8Message message in ft8.Messages)
            messages.Add(new DigiAnalysisLine("FT8", message.Text, message.AudioHz, message.SnrDb));
        foreach (Js8Message message in js8)
            messages.Add(new DigiAnalysisLine("JS8", message.Text, message.AudioHz, message.SnrDb));

        bool selfTestOk = RunSelfTest(audio, sampleRate, modeHint);
        var hints = BuildHints(
            duration, rms, spectrum, ft8, js8.Count, varacAvailable, varacStatus, selfTestOk, modeHint);

        return new DigiAnalysisReport(
            sampleRate,
            Math.Round(duration, 2),
            Math.Round(rms, 5),
            Math.Round(peak, 4),
            spectrum.HotBins,
            spectrum.PeakHz,
            spectrum.WidthHz,
            spectrum.Entropy,
            ft8.Messages.Count,
            ft8.BestSyncScore,
            ft8.LdpcAccepted,
            js8.Count,
            varacAvailable,
            varacStatus,
            selfTestOk,
            messages,
            hints);
    }

    public static DigiAnalysisReport AnalyzeWav(ReadOnlySpan<byte> wav, string? modeHint = null)
    {
        if (!WavFile.TryRead(wav, out float[] samples, out int rate))
        {
            return new DigiAnalysisReport(
                0, 0, 0, 0, 0, null, null, null,
                0, 0, 0, 0, false, null, false, [],
                ["The clip was not a readable 8/16-bit mono WAV."]);
        }

        return Analyze(samples, rate, modeHint);
    }

    private static bool RunSelfTest(ReadOnlySpan<float> audio, int sampleRate, string? modeHint)
    {
        string mode = (modeHint ?? "").Trim().ToLowerInvariant();
        var probe = audio.ToArray();
        for (int i = 0; i < probe.Length; i++)
            probe[i] *= 0.12f;

        if (mode is "js8" or "js8call")
        {
            if (probe.Length < Js8Decoder.SampleRate * 13)
                return false;
            try
            {
                Js8Decoder.Modulate(probe, sampleRate, "KN4CRD: @ALLCALL CQ CQ CQ EM73", 1_200, 0.5);
            }
            catch (ArgumentException)
            {
                return false;
            }

            return Js8Decoder.Decode(probe, sampleRate).Count > 0;
        }

        if (probe.Length < Ft8Decoder.SampleRate * 10)
            return false;
        Ft8Decoder.Modulate(probe, sampleRate, "CQ K1ABC FN42", 1_200, 0.5);
        return Ft8Decoder.Decode(probe, sampleRate).Count > 0;
    }

    private static List<string> BuildHints(
        double duration,
        double rms,
        SpectrumStats spectrum,
        Ft8DecodeStats ft8,
        int js8Count,
        bool varacAvailable,
        string? varacStatus,
        bool selfTestOk,
        string? modeHint)
    {
        var hints = new List<string>();
        string mode = (modeHint ?? "").Trim().ToLowerInvariant();

        if (duration < 10)
            hints.Add("Clip is shorter than about 10 s; a full FT8/JS8 frame needs ~12.6 s.");
        if (rms < 0.002)
            hints.Add("Audio level is very low.");
        if (spectrum.WidthHz is double width && width > 200)
            hints.Add($"Dominant energy is ~{width:0} Hz wide; a single FT8/JS8 tone stack is about 44 Hz.");
        if (spectrum.WidthHz is double narrow && narrow < 15 && spectrum.PeakHz is not null)
            hints.Add("Dominant energy looks like a narrow carrier/spur, not an 8-FSK digimode stack.");
        if (ft8.Messages.Count == 0 && js8Count == 0)
        {
            if (ft8.BestSyncScore < 6)
                hints.Add($"FT8 Costas sync is weak (best {ft8.BestSyncScore:0.0}); no solid FT8 frame lock.");
            else if (ft8.LdpcAccepted == 0)
                hints.Add("FT8 Costas candidates appeared but LDPC/CRC failed.");
        }

        if ((mode is "js8" or "js8call") && js8Count == 0)
            hints.Add("JS8 decoder found no Normal-speed frames in this clip.");
        if (mode is "varac" or "vara" or "xara" or "zara")
        {
            if (!varacAvailable)
                hints.Add("xara/zara monitor is not available in this process.");
            else if (!string.IsNullOrWhiteSpace(varacStatus) && varacStatus.Contains("no complete ARQ", StringComparison.OrdinalIgnoreCase))
                hints.Add("VarAC monitor ran but produced no complete ARQ text (occupancy is not a decode).");
        }

        if (selfTestOk)
            hints.Add("Decoder self-test into this clip succeeded, so the decode path can still read clean digimode audio.");
        else
            hints.Add("Decoder self-test into this clip failed; check sample rate and clip length.");

        if (hints.Count == 0)
            hints.Add("No obvious decode blocker from the audio diagnostics.");
        return hints;
    }

    private static SpectrumStats MeasureSpectrum(ReadOnlySpan<float> audio, int sampleRate)
    {
        int n = 1 << (int)Math.Floor(Math.Log2(Math.Min(audio.Length, 65_536)));
        if (n < 256)
            return new SpectrumStats(0, null, null, null);

        var re = new double[n];
        var im = new double[n];
        for (int i = 0; i < n; i++)
            re[i] = audio[i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / Math.Max(1, n - 1)));
        Fft(re, im);

        var db = new double[n / 2];
        for (int i = 0; i < db.Length; i++)
            db[i] = 10 * Math.Log10(re[i] * re[i] + im[i] * im[i] + 1e-20);

        var band = new List<double>();
        for (int i = 0; i < db.Length; i++)
        {
            double hz = i * sampleRate / (double)n;
            if (hz is >= 200 and <= 3000)
                band.Add(db[i]);
        }

        if (band.Count == 0)
            return new SpectrumStats(0, null, null, null);

        var sorted = band.ToArray();
        Array.Sort(sorted);
        double noise = sorted[(int)(0.2 * (sorted.Length - 1))];
        int hot = 0;
        double bestDb = double.NegativeInfinity;
        double bestHz = 0;
        for (int i = 0; i < db.Length; i++)
        {
            double hz = i * sampleRate / (double)n;
            if (hz is < 200 or > 3000)
                continue;
            if (db[i] >= noise + 3)
                hot++;
            if (db[i] > bestDb)
            {
                bestDb = db[i];
                bestHz = hz;
            }
        }

        double lo = bestHz, hi = bestHz;
        for (int i = 0; i < db.Length; i++)
        {
            double hz = i * sampleRate / (double)n;
            if (hz is < 200 or > 3000)
                continue;
            if (db[i] >= bestDb - 10)
            {
                lo = Math.Min(lo, hz);
                hi = Math.Max(hi, hz);
            }
        }

        double? entropy = ToneEntropy(audio, sampleRate, bestHz);
        return new SpectrumStats(hot, bestHz, hi - lo, entropy);
    }

    private static double? ToneEntropy(ReadOnlySpan<float> audio, int sampleRate, double peakHz)
    {
        int sps = (int)Math.Round(sampleRate * 0.16);
        if (sps < 16 || audio.Length < sps * 8)
            return null;

        int symbols = Math.Min(79, (audio.Length - sps) / sps);
        var winners = new int[8];
        for (int s = 0; s < symbols; s++)
        {
            int start = s * sps;
            int best = 0;
            double bestP = double.NegativeInfinity;
            for (int t = 0; t < 8; t++)
            {
                double f = peakHz - 3 * 6.25 + t * 6.25;
                double p = Goertzel(audio.Slice(start, sps), sampleRate, f);
                if (p > bestP)
                {
                    bestP = p;
                    best = t;
                }
            }

            winners[best]++;
        }

        double entropy = 0;
        for (int t = 0; t < 8; t++)
        {
            if (winners[t] == 0)
                continue;
            double p = winners[t] / (double)symbols;
            entropy -= p * Math.Log(p, 2);
        }

        return Math.Round(entropy, 2);
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

    private static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        int j = 0;
        for (int i = 1; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;
            j ^= bit;
            if (i >= j)
                continue;
            (re[i], re[j]) = (re[j], re[i]);
            (im[i], im[j]) = (im[j], im[i]);
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            double wlenRe = Math.Cos(ang);
            double wlenIm = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double wRe = 1;
                double wIm = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int u = i + k;
                    int v = i + k + len / 2;
                    double tRe = wRe * re[v] - wIm * im[v];
                    double tIm = wRe * im[v] + wIm * re[v];
                    re[v] = re[u] - tRe;
                    im[v] = im[u] - tIm;
                    re[u] += tRe;
                    im[u] += tIm;
                    double nwRe = wRe * wlenRe - wIm * wlenIm;
                    wIm = wRe * wlenIm + wIm * wlenRe;
                    wRe = nwRe;
                }
            }
        }
    }

    private readonly record struct SpectrumStats(int HotBins, double? PeakHz, double? WidthHz, double? Entropy);
}
