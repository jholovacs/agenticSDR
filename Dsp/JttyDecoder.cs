using System.Text;

namespace AgenticSdr.Dsp;

/// <summary>One JTTY print line with tone pitch and strength.</summary>
public sealed record JttyMessage(string Text, double AudioHz, double SnrDb);

/// <summary>
/// JTTY receive: 31.25 baud 4-tone GFSK about 127 Hz wide (WSJT-X keyboard-style footprint).
/// Symbols carry two bits; seven bits make one ASCII character. Strong, clean copy prints text;
/// weak or unsynced audio stays quiet rather than inventing letters.
/// </summary>
public static class JttyDecoder
{
    public const int SampleRate = 12_000;
    public const double Baud = 31.25;
    public const int Tones = 4;
    public const double SpanHz = 127;

    public static string Format(IReadOnlyList<JttyMessage> messages)
    {
        if (messages.Count == 0)
            return "";
        var lines = new List<string> { "JTTY" };
        foreach (JttyMessage message in messages)
        {
            lines.Add($"{message.SnrDb,4:0} dB  {message.AudioHz,6:0} Hz  {message.Text}");
            lines.Add($"           {Explain(message.Text)}");
        }

        return string.Join("\n", lines);
    }

    public static string Explain(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "JTTY message.";
        string t = text.Trim();
        if (t.StartsWith("CQ ", StringComparison.OrdinalIgnoreCase))
            return "JTTY CQ — calling for a keyboard contact.";
        if (t.StartsWith("TU ", StringComparison.OrdinalIgnoreCase))
            return "Thank you / end of exchange.";
        if (t.Contains("599", StringComparison.Ordinal))
            return "Contest-style signal report and serial.";
        return "JTTY keyboard text (4-tone GFSK).";
    }

    /// <summary>Synthesize a JTTY-like 4-tone stream carrying 7-bit ASCII (for tests).</summary>
    public static void Modulate(Span<float> audio, int sampleRate, string text, double centerHz = 1_000, float amplitude = 0.45f)
    {
        if (audio.IsEmpty || sampleRate < 4_000 || string.IsNullOrEmpty(text))
            return;

        int sps = Math.Max(4, (int)Math.Round(sampleRate / Baud));
        double spacing = SpanHz / (Tones - 1);
        var tones = new double[Tones];
        for (int t = 0; t < Tones; t++)
            tones[t] = centerHz - 0.5 * SpanHz + t * spacing;

        var bits = new List<int>(text.Length * 7);
        foreach (char ch in text)
        {
            int value = ch & 0x7F;
            for (int b = 0; b < 7; b++)
                bits.Add((value >> b) & 1);
        }

        if (bits.Count % 2 != 0)
            bits.Add(0);

        int at = 0;
        double phase = 0;
        for (int i = 0; i + 1 < bits.Count && at < audio.Length; i += 2)
        {
            int symbol = (bits[i] << 1) | bits[i + 1];
            double freq = tones[Math.Clamp(symbol, 0, Tones - 1)];
            double step = 2 * Math.PI * freq / sampleRate;
            for (int n = 0; n < sps && at < audio.Length; n++, at++)
            {
                audio[at] += amplitude * (float)Math.Sin(phase);
                phase += step;
                if (phase > Math.PI)
                    phase -= 2 * Math.PI;
            }
        }
    }

    public static IReadOnlyList<JttyMessage> Decode(ReadOnlySpan<float> audio, int sampleRate)
    {
        if (audio.Length < sampleRate / 2 || sampleRate < 4_000)
            return [];

        float[] wave = Resample(audio, sampleRate, SampleRate);
        int sps = Math.Max(8, (int)Math.Round(SampleRate / Baud));
        double spacing = SpanHz / (Tones - 1);

        var hits = new List<JttyMessage>();
        for (double center = 400; center <= 2_400; center += 25)
        {
            if (!TryDecodeAt(wave, center, spacing, sps, out string text, out double snr))
                continue;
            if (text.Length < 3)
                continue;
            hits.Add(new JttyMessage(text, center, snr));
        }

        return hits
            .GroupBy(message => (int)(message.AudioHz / 40))
            .Select(group => group.OrderByDescending(message => ScoreText(message.Text)).First())
            .Where(message => ScoreText(message.Text) >= 8 && LooksLikeCopy(message.Text))
            .OrderByDescending(message => ScoreText(message.Text))
            .ThenBy(message => message.AudioHz)
            .Take(4)
            .ToList();
    }

    private static bool TryDecodeAt(float[] wave, double centerHz, double spacing, int sps, out string text, out double snrDb)
    {
        text = "";
        snrDb = -40;
        var tones = new double[Tones];
        for (int t = 0; t < Tones; t++)
            tones[t] = centerHz - 0.5 * SpanHz + t * spacing;

        string bestText = "";
        double bestScore = 0;
        double bestSnr = -40;

        // Search symbol phase so we land on the same edges Modulate used.
        int phaseStep = Math.Max(1, sps / 8);
        for (int phase = 0; phase < sps; phase += phaseStep)
        {
            var symbols = new List<int>();
            var peakPowers = new List<double>();
            var otherPowers = new List<double>();
            for (int start = phase; start + sps <= wave.Length; start += sps)
            {
                int best = 0;
                double bestP = double.NegativeInfinity;
                double sum = 0;
                for (int t = 0; t < Tones; t++)
                {
                    double p = Goertzel(wave.AsSpan(start, sps), SampleRate, tones[t]);
                    sum += p;
                    if (p > bestP)
                    {
                        bestP = p;
                        best = t;
                    }
                }

                symbols.Add(best);
                peakPowers.Add(bestP);
                otherPowers.Add(Math.Max(1e-18, (sum - bestP) / Math.Max(1, Tones - 1)));
            }

            if (symbols.Count < 21)
                continue;

            double meanPeak = peakPowers.Average();
            double meanOther = Math.Max(1e-18, otherPowers.Average());
            double snr = Math.Clamp(10 * Math.Log10(meanPeak / meanOther), -20, 40);
            // Need a clear 4-tone preference; leakage at nearby centers otherwise invents letters.
            if (snr < 9)
                continue;

            var bits = new List<int>(symbols.Count * 2);
            foreach (int symbol in symbols)
            {
                bits.Add((symbol >> 1) & 1);
                bits.Add(symbol & 1);
            }

            // Character framing is 7 bits; try every alignment.
            for (int bitOffset = 0; bitOffset < 7; bitOffset++)
            {
                string candidate = BitsToAscii(bits, bitOffset);
                double score = ScoreText(candidate);
                if (score <= bestScore)
                    continue;
                bestScore = score;
                bestText = candidate;
                bestSnr = snr;
            }
        }

        if (bestScore < 8 || bestText.Length < 3 || !LooksLikeCopy(bestText))
            return false;

        text = bestText;
        snrDb = bestSnr;
        return true;
    }

    private static string BitsToAscii(List<int> bits, int bitOffset)
    {
        var sb = new StringBuilder();
        for (int i = bitOffset; i + 7 <= bits.Count; i += 7)
        {
            int value = 0;
            for (int b = 0; b < 7; b++)
                value |= bits[i + b] << b;
            char ch = (char)(value & 0x7F);
            if (ch is >= ' ' and <= '~')
                sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != ' ')
                sb.Append(' ');
        }

        return Collapse(sb.ToString());
    }

    private static double ScoreText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;
        int letters = text.Count(ch => char.IsLetterOrDigit(ch));
        int spaces = text.Count(ch => ch == ' ');
        int printable = text.Count(ch => ch is >= ' ' and <= '~');
        if (printable == 0 || letters < 3)
            return 0;
        double ratio = letters / (double)printable;
        if (ratio < 0.7)
            return 0;
        double score = letters * ratio + spaces * 2;
        if (text.Contains("CQ", StringComparison.OrdinalIgnoreCase))
            score += 6;
        if (text.Contains("DE", StringComparison.OrdinalIgnoreCase))
            score += 4;
        return score;
    }

    private static bool LooksLikeCopy(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 3)
            return false;
        int letters = text.Count(char.IsLetterOrDigit);
        int weird = text.Count(ch => ch is < ' ' or > '~' or '*' or '=' or ']' or '[' or '_' or '.');
        if (letters < 3)
            return false;
        if (weird > letters / 3)
            return false;
        // Prefer spaced chat / callsign tokens over a solid random alphabet soup.
        if (text.Contains(' ', StringComparison.Ordinal))
            return true;
        return text.Length <= 12 && text.Count(char.IsLetter) >= 2 && text.Any(char.IsDigit);
    }

    private static string Collapse(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool space = false;
        foreach (char ch in text.Trim())
        {
            if (char.IsWhiteSpace(ch))
            {
                space = true;
                continue;
            }

            if (space && sb.Length > 0)
                sb.Append(' ');
            space = false;
            sb.Append(ch);
        }

        return sb.ToString();
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
}
