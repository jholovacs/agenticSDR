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
