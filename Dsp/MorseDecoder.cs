using System.Text;

namespace AgenticSdr.Dsp;

/// <summary>
/// Reads Morse timing from audio. It follows the loudness, estimates the dit length from the marks it heard,
/// and prints letters and digits. A clip with no Morse returns an empty string.
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
        ["....."] = '5', ["-...."] = '6', ["--..."] = '7', ["---.."] = '8', ["----."] = '9'
    };

    public static string Decode(ReadOnlySpan<float> audio, int sampleRate)
    {
        if (audio.Length < sampleRate / 20 || sampleRate < 1000)
            return "";

        var env = new float[audio.Length];
        float smooth = 0;
        float alpha = 1f - (float)Math.Exp(-1.0 / (sampleRate * 0.008));
        for (int i = 0; i < audio.Length; i++)
        {
            smooth += alpha * (Math.Abs(audio[i]) - smooth);
            env[i] = smooth;
        }

        var sorted = (float[])env.Clone();
        Array.Sort(sorted);
        float low = sorted[(int)(sorted.Length * 0.2)];
        float high = sorted[(int)(sorted.Length * 0.9)];
        if (high < 0.02f || high < low * 1.8f)
            return "";

        float threshold = low + 0.45f * (high - low);
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
        runs = Clean(runs, (int)(sampleRate * 0.015));

        var marks = runs.Where(r => r.Mark).Select(r => r.Samples).ToList();
        if (marks.Count == 0)
            return "";

        int dit = EstimateDit(marks);
        if (dit <= 0)
            return "";

        var text = new StringBuilder();
        var symbol = new StringBuilder();
        foreach (var run in runs)
        {
            double units = (double)run.Samples / dit;
            if (run.Mark)
            {
                symbol.Append(units > 1.8 ? '-' : '.');
                continue;
            }

            if (units >= 5)
            {
                Flush(text, symbol);
                if (text.Length > 0 && text[^1] != ' ')
                    text.Append(' ');
            }
            else if (units >= 2)
            {
                Flush(text, symbol);
            }
        }

        Flush(text, symbol);
        return text.ToString().Trim();
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
        if (peak < 1e-5f || peak >= 0.05f)
            return "";

        float gain = 0.5f / peak;
        for (int i = 0; i < narrow.Length; i++)
            narrow[i] *= gain;
        string boosted = Decode(narrow, sampleRate);
        return IsCopy(boosted) ? boosted : "";
    }

    private static bool TryTone(ReadOnlySpan<float> audio, int sampleRate, out double hz)
    {
        hz = 0;
        int n = Math.Min(audio.Length, sampleRate);
        if (n < sampleRate / 5 || sampleRate < 1000)
            return false;

        int start = audio.Length - n;
        const double lowHz = 200;
        const double highHz = 1_500;
        const double stepHz = 20;
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
        int away = Math.Max(1, (int)Math.Round(100 / stepHz));
        double shoulder = 0;
        if (peak - away >= 0)
            shoulder = power[peak - away];
        if (peak + away < bins)
            shoulder = Math.Max(shoulder, power[peak + away]);
        if (median <= 0 || power[peak] < median * 8 || power[peak] < shoulder * 4)
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
        double bandwidth = Math.Clamp(hz / 4, 80, 180);
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
        return letters >= 2 && unknown * 4 <= symbols;
    }

    private static int EstimateDit(List<int> marks)
    {
        var sorted = marks.OrderBy(v => v).ToList();
        if (sorted.Count == 1)
            return sorted[0];
        if (sorted[^1] <= sorted[0] * 2)
            return sorted[sorted.Count / 2];

        int split = 1;
        double bestRatio = 1;
        for (int i = 1; i < sorted.Count; i++)
        {
            double ratio = (double)sorted[i] / sorted[i - 1];
            if (ratio > bestRatio)
            {
                bestRatio = ratio;
                split = i;
            }
        }

        return sorted[split / 2];
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
}
