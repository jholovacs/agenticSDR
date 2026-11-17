using System.Text;

namespace AgenticSdr.Dsp;

/// <summary>
/// Reads Morse timing from audio. It follows the loudness, estimates the dit length from the marks it heard,
/// and prints letters and digits. A clip with no Morse returns an empty string.
/// Quiet CW is amplified before keying is measured so a weak HF carrier still copies.
/// </summary>
public static class MorseDecoder
{
    private static readonly Dictionary<string, char> Letters = new(StringComparer.Ordinal)
    {
        [".-"] = 'A', ["-..."] = 'B', ["-.-."] = 'C', ["-.."] = 'D', ["."] = 'E',
        ["..-."] = 'F', ["--."] = 'G', ["...."] = 'H', [".."] = 'I', [".---"] = 'J',
        ["-.-"] = 'K', [".-.."] = 'L', ["--"] = 'M', ["-."] = 'N', ["---"] = 'O',
        [".--."] = 'P', ["--.-"] = 'Q', [".-."] = 'R', ["..."] = 'S', ["-"] = 'T',
        ["..-"] = 'U', ["...-"] = 'V', [".--"] = 'W', ["-..-"] = 'X', ["-.--"] = 'Y',
        ["--.."] = 'Z',
        ["-----"] = '0', [".----"] = '1', ["..---"] = '2', ["...--"] = '3', ["....-"] = '4',
        ["....."] = '5', ["-...."] = '6', ["--..."] = '7', ["---.."] = '8', ["----."] = '9',
        [".-.-."] = '+', // AR
        ["-...-"] = '=', // BT
        ["...-.-"] = '*', // SK (rendered *)
        [".-.-"] = ',', // rarely used
        ["--..--"] = ',',
        ["..--.."] = '?',
        ["-....-"] = '-',
        [".-..-."] = '"',
        ["-.-.--"] = '!',
        [".----."] = '\'',
        ["-..-."] = '/',
    };

    /// <summary>
    /// Best-effort CW decode for demodulated Morse audio (BFO tone or raw keying).
    /// Tries a direct envelope read, then a tone-isolated path for quiet signals.
    /// </summary>
    public static string DecodeCw(ReadOnlySpan<float> audio, int sampleRate)
    {
        string direct = Decode(audio, sampleRate);
        if (IsCopy(direct))
            return direct;

        string audible = DecodeAudible(audio, sampleRate);
        if (IsCopy(audible))
            return audible;

        // Prefer the longer almost-copy when neither quite passes IsCopy.
        if (Score(audible) > Score(direct) && Score(audible) >= 2)
            return audible;
        return Score(direct) >= 2 ? direct : "";
    }

    /// <summary>
    /// Synthesize keyed CW tone audio for <paramref name="text"/>. Letters, digits, and common punctuation
    /// from the decoder table are sent; spaces become word gaps. Unknown characters are skipped.
    /// This is baseband audio only — it does not key a radio.
    /// </summary>
    public static float[] Modulate(string text, int sampleRate = 8_000, double toneHz = 700, double wordsPerMinute = 20, float amplitude = 0.55f)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("CW text is empty.", nameof(text));
        if (sampleRate < 1_000)
            throw new ArgumentException("Sample rate is too low for CW.", nameof(sampleRate));
        if (!(wordsPerMinute is >= 5 and <= 60))
            throw new ArgumentException("CW speed has to be between 5 and 60 WPM.", nameof(wordsPerMinute));
        if (!(toneHz is >= 300 and <= 1_200))
            throw new ArgumentException("CW tone has to be between 300 and 1200 Hz.", nameof(toneHz));

        EnsureEncodeTable();
        double ditSeconds = 1.2 / wordsPerMinute;
        int dit = Math.Max(1, (int)Math.Round(sampleRate * ditSeconds));
        var marks = new List<(bool On, int Dits)>();
        bool first = true;
        foreach (char raw in text.Trim().ToUpperInvariant())
        {
            if (char.IsWhiteSpace(raw))
            {
                marks.Add((false, 7));
                first = true;
                continue;
            }

            if (Encode is null || !Encode.TryGetValue(raw, out string? pattern) || string.IsNullOrEmpty(pattern))
                continue;

            if (!first)
                marks.Add((false, 3));
            first = false;
            for (int i = 0; i < pattern.Length; i++)
            {
                if (i > 0)
                    marks.Add((false, 1));
                marks.Add((true, pattern[i] == '-' ? 3 : 1));
            }
        }

        if (marks.Count == 0)
            throw new ArgumentException("CW text has no Morse characters that can be sent.", nameof(text));

        marks.Add((false, 7));
        int totalDits = 0;
        foreach ((bool _, int count) in marks)
            totalDits += count;
        var audio = new float[totalDits * dit];
        double phase = 0;
        double step = 2 * Math.PI * toneHz / sampleRate;
        int cursor = 0;
        foreach ((bool on, int count) in marks)
        {
            int samples = count * dit;
            for (int i = 0; i < samples && cursor < audio.Length; i++, cursor++)
            {
                if (on)
                {
                    // Raised-cosine edges on each element so the tone is less clicky in a speaker.
                    float edge = 1f;
                    int fade = Math.Min(dit / 4, Math.Min(i, samples - 1 - i));
                    if (fade < dit / 4)
                        edge = 0.5f - 0.5f * (float)Math.Cos(Math.PI * fade / Math.Max(1, dit / 4));
                    audio[cursor] = amplitude * edge * (float)Math.Cos(phase);
                    phase += step;
                    if (phase > Math.PI)
                        phase -= 2 * Math.PI;
                }
            }
        }

        return audio;
    }

    public static string Decode(ReadOnlySpan<float> audio, int sampleRate)
    {
        if (audio.Length < sampleRate / 20 || sampleRate < 1000)
            return "";

        var env = Envelope(audio, sampleRate);
        Agc(env);

        string best = "";
        int bestScore = -1;
        // Several thresholds: HF CW keying depth varies with QSB and AGC.
        ReadOnlySpan<float> fracs = [0.30f, 0.40f, 0.50f, 0.60f];
        foreach (float frac in fracs)
        {
            string text = DecodeEnvelope(env, sampleRate, frac);
            int score = Score(text);
            if (score > bestScore)
            {
                bestScore = score;
                best = text;
            }
        }

        return best;
    }

    /// <summary>
    /// Reads Morse from sideband audio. A keyed carrier is one tone inside a wide voice channel,
    /// so the loudest pitch is isolated before the keying is measured. A quiet tone is raised
    /// to a listenable level. Audio with no single tone returns an empty string.
    /// </summary>
    public static string DecodeAudible(ReadOnlySpan<float> audio, int sampleRate)
    {
        if (!TryTone(audio, sampleRate, out double hz))
            return "";

        float[] narrow = Bandpass(audio, sampleRate, hz);
        string direct = Decode(narrow, sampleRate);
        if (IsCopy(direct))
            return direct;

        float peak = 0;
        foreach (float sample in narrow)
            peak = Math.Max(peak, Math.Abs(sample));
        if (peak < 1e-6f)
            return "";

        float gain = 0.5f / peak;
        for (int i = 0; i < narrow.Length; i++)
            narrow[i] *= gain;
        string boosted = Decode(narrow, sampleRate);
        return IsCopy(boosted) ? boosted : (Score(boosted) > Score(direct) ? boosted : direct);
    }

    private static float[] Envelope(ReadOnlySpan<float> audio, int sampleRate)
    {
        var env = new float[audio.Length];
        float smooth = 0;
        float alpha = 1f - (float)Math.Exp(-1.0 / (sampleRate * 0.005));
        for (int i = 0; i < audio.Length; i++)
        {
            smooth += alpha * (Math.Abs(audio[i]) - smooth);
            env[i] = smooth;
        }

        return env;
    }

    /// <summary>Stretch the envelope so quiet CW still crosses a stable threshold.</summary>
    private static void Agc(Span<float> env)
    {
        var sorted = env.ToArray();
        Array.Sort(sorted);
        float high = sorted[(int)(sorted.Length * 0.95)];
        if (high < 1e-8f)
            return;
        float gain = 0.5f / high;
        for (int i = 0; i < env.Length; i++)
            env[i] *= gain;
    }

    private static string DecodeEnvelope(float[] env, int sampleRate, float thresholdFrac)
    {
        var sorted = (float[])env.Clone();
        Array.Sort(sorted);
        float low = sorted[(int)(sorted.Length * 0.15)];
        float high = sorted[(int)(sorted.Length * 0.90)];
        if (high < 0.01f || high < low * 1.35f)
            return "";

        float threshold = low + thresholdFrac * (high - low);
        var runs = new List<(bool Mark, int Samples)>();
        bool mark = env[0] >= threshold;
        int length = 1;
        for (int i = 1; i < env.Length; i++)
        {
            bool next = env[i] >= threshold;
            if (next == mark)
            {
                length++;
                continue;
            }

            runs.Add((mark, length));
            mark = next;
            length = 1;
        }

        runs.Add((mark, length));
        // Ignore clicks shorter than ~8 ms; keep true dits (≈50–80 ms at 15–25 WPM).
        runs = Clean(runs, (int)(sampleRate * 0.008));

        var marks = runs.Where(r => r.Mark).Select(r => r.Samples).ToList();
        if (marks.Count < 3)
            return "";

        int dit = EstimateDit(marks, sampleRate);
        if (dit <= 0)
            return "";

        var text = new StringBuilder();
        var symbol = new StringBuilder();
        foreach (var run in runs)
        {
            double units = (double)run.Samples / dit;
            if (run.Mark)
            {
                // Dah ≈ 3 dits; allow 2.0–2.2 for slightly short dahs on HF.
                symbol.Append(units >= 2.0 ? '-' : '.');
                continue;
            }

            if (units >= 5.5)
            {
                Flush(text, symbol);
                if (text.Length > 0 && text[^1] != ' ')
                    text.Append(' ');
            }
            else if (units >= 2.2)
            {
                Flush(text, symbol);
            }
        }

        Flush(text, symbol);
        return text.ToString().Trim();
    }

    private static bool TryTone(ReadOnlySpan<float> audio, int sampleRate, out double hz)
    {
        hz = 0;
        int n = Math.Min(audio.Length, sampleRate);
        if (n < sampleRate / 5 || sampleRate < 1000)
            return false;

        int start = Math.Max(0, audio.Length - n);
        // Prefer mid-clip if the start is tuner settle / AGC.
        if (audio.Length > sampleRate * 2)
            start = audio.Length / 2 - n / 2;

        const double lowHz = 200;
        const double highHz = 1_800;
        const double stepHz = 10;
        int bins = (int)((highHz - lowHz) / stepHz) + 1;
        var power = new double[bins];
        for (int bin = 0; bin < bins; bin++)
            power[bin] = Goertzel(audio.Slice(start, n), sampleRate, lowHz + bin * stepHz);

        int peak = 0;
        for (int bin = 1; bin < bins; bin++)
        {
            if (power[bin] > power[peak])
                peak = bin;
        }

        var sorted = (double[])power.Clone();
        Array.Sort(sorted);
        double median = sorted[bins / 2];
        int away = Math.Max(1, (int)Math.Round(80 / stepHz));
        double shoulder = 0;
        if (peak - away >= 0)
            shoulder = power[peak - away];
        if (peak + away < bins)
            shoulder = Math.Max(shoulder, power[peak + away]);
        if (median <= 0 || power[peak] < median * 5 || power[peak] < shoulder * 2.5)
            return false;

        hz = lowHz + peak * stepHz;
        return true;
    }

    private static double Goertzel(ReadOnlySpan<float> audio, int sampleRate, double hz)
    {
        double coeff = 2 * Math.Cos(2 * Math.PI * hz / sampleRate);
        double s1 = 0;
        double s2 = 0;
        int n = audio.Length;
        for (int i = 0; i < n; i++)
        {
            double window = 0.5 * (1 - Math.Cos(2 * Math.PI * i / Math.Max(1, n - 1)));
            double s0 = audio[i] * window + coeff * s1 - s2;
            s2 = s1;
            s1 = s0;
        }

        return s1 * s1 + s2 * s2 - coeff * s1 * s2;
    }

    private static float[] Bandpass(ReadOnlySpan<float> audio, int sampleRate, double hz)
    {
        double bandwidth = Math.Clamp(hz / 5, 60, 150);
        double q = hz / bandwidth;
        double w0 = 2 * Math.PI * hz / sampleRate;
        double alpha = Math.Sin(w0) / (2 * q);
        double b0 = alpha;
        double b2 = -alpha;
        double a0 = 1 + alpha;
        double a1 = -2 * Math.Cos(w0);
        double a2 = 1 - alpha;
        var output = new float[audio.Length];
        double x1 = 0;
        double x2 = 0;
        double y1 = 0;
        double y2 = 0;
        for (int i = 0; i < audio.Length; i++)
        {
            double x0 = audio[i];
            double y0 = (b0 * x0 + b2 * x2 - a1 * y1 - a2 * y2) / a0;
            output[i] = (float)y0;
            x2 = x1;
            x1 = x0;
            y2 = y1;
            y1 = y0;
        }

        return output;
    }

    /// <summary>True when the text has real letters and few unrecognized marks.</summary>
    public static bool IsCopy(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        return Score(text) >= 4 && UnknownRatio(text) <= 0.35;
    }

    private static int Score(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;
        int letters = 0;
        int unknown = 0;
        foreach (char ch in text)
        {
            if (ch == ' ')
                continue;
            if (char.IsLetterOrDigit(ch))
                letters++;
            else
                unknown++;
        }

        return letters - unknown * 2;
    }

    private static double UnknownRatio(string text)
    {
        int letters = 0;
        int unknown = 0;
        foreach (char ch in text)
        {
            if (ch == ' ')
                continue;
            if (char.IsLetterOrDigit(ch))
                letters++;
            else
                unknown++;
        }

        int symbols = letters + unknown;
        return symbols == 0 ? 1 : unknown / (double)symbols;
    }

    private static int EstimateDit(List<int> marks, int sampleRate)
    {
        // Typical HF CW: about 12–40 WPM → dit ≈ 30–100 ms.
        int minDit = (int)(sampleRate * 0.025);
        int maxDit = (int)(sampleRate * 0.120);
        var candidates = marks.Where(v => v >= minDit && v <= maxDit * 1.4).OrderBy(v => v).ToList();
        if (candidates.Count == 0)
            candidates = marks.OrderBy(v => v).ToList();

        if (candidates.Count == 1)
            return Math.Clamp(candidates[0], minDit, maxDit);

        // Cluster short marks (dits). Dahs are ~3×; find the largest gap in the lower half.
        if (candidates[^1] <= candidates[0] * 2.2)
            return Math.Clamp(candidates[candidates.Count / 2], minDit, maxDit);

        int split = 1;
        double bestRatio = 1;
        for (int i = 1; i < candidates.Count; i++)
        {
            double ratio = (double)candidates[i] / candidates[i - 1];
            if (ratio > bestRatio)
            {
                bestRatio = ratio;
                split = i;
            }
        }

        int dit = candidates[Math.Max(0, split / 2)];
        // If the "dit" cluster looks like dahs, divide by 3.
        double medianAll = marks.OrderBy(v => v).ElementAt(marks.Count / 2);
        if (dit > maxDit && medianAll / 3 >= minDit && medianAll / 3 <= maxDit)
            dit = (int)Math.Round(medianAll / 3.0);
        return Math.Clamp(dit, minDit, maxDit);
    }

    private static List<(bool Mark, int Samples)> Clean(List<(bool Mark, int Samples)> runs, int minSamples)
    {
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int i = 0; i < runs.Count; i++)
            {
                if (runs[i].Samples >= minSamples)
                    continue;

                if (i > 0 && i < runs.Count - 1)
                {
                    runs[i - 1] = (runs[i - 1].Mark, runs[i - 1].Samples + runs[i].Samples + runs[i + 1].Samples);
                    runs.RemoveRange(i, 2);
                }
                else if (i == 0 && runs.Count > 1)
                {
                    runs[1] = (runs[1].Mark, runs[0].Samples + runs[1].Samples);
                    runs.RemoveAt(0);
                }
                else if (i == runs.Count - 1 && runs.Count > 1)
                {
                    runs[i - 1] = (runs[i - 1].Mark, runs[i - 1].Samples + runs[i].Samples);
                    runs.RemoveAt(i);
                }
                else
                {
                    runs.RemoveAt(i);
                }

                changed = true;
                break;
            }
        }

        return runs;
    }

    private static void Flush(StringBuilder text, StringBuilder symbol)
    {
        if (symbol.Length == 0)
            return;
        text.Append(Letters.TryGetValue(symbol.ToString(), out char letter) ? letter : '?');
        symbol.Clear();
    }

    static readonly object EncodeGate = new();
    static Dictionary<char, string>? Encode;

    static void EnsureEncodeTable()
    {
        if (Encode is not null)
            return;
        lock (EncodeGate)
        {
            if (Encode is not null)
                return;
            var map = new Dictionary<char, string>();
            foreach ((string pattern, char letter) in Letters)
            {
                // Prefer the first pattern when a character has aliases (comma).
                if (!map.ContainsKey(letter))
                    map[letter] = pattern;
            }
            Encode = map;
        }
    }
}
