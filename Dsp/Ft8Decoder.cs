using System.Numerics;
using MathNet.Numerics.IntegralTransforms;

namespace AgenticSdr.Dsp;

/// <summary>
/// One decoded FT8 line. AudioHz is where the tone sat in the audio. TimeSeconds is when the 15-second slot started. SnrDb is the estimated strength.
/// </summary>
public sealed record Ft8Message(string Text, double AudioHz, double TimeSeconds, double SnrDb);

/// <summary>Decode outcome plus sync/LDPC counters for empty-slot diagnosis.</summary>
public sealed record Ft8DecodeStats(
    IReadOnlyList<Ft8Message> Messages,
    int CandidatesTried,
    int LdpcAccepted,
    float BestSyncScore,
    double AudioSeconds);

/// <summary>
/// Finds FT8 messages in audio. FT8 is a slow digital mode: 15-second transmissions built from eight tones.
/// Audio shorter than about 10 seconds returns nothing, because a whole message has not arrived.
/// </summary>
public static class Ft8Decoder
{
    public const int SampleRate = 12_000;
    private const int SymbolSamples = 1_920;
    private const int TimeOsr = 4;
    private const int Hop = SymbolSamples / TimeOsr;
    private const int FreqOsr = 2;
    private const int FftSize = SymbolSamples * FreqOsr;
    private const int MinBin = 32;
    private const int MaxBin = 481;
    private const int NumBins = MaxBin - MinBin;
    /// <summary>How many Costas hits to try with LDPC. A busy band needs far more than a handful.</summary>
    private const int MaxCandidates = 200;
    private const float MinSyncScore = 1.6f;
    private const int DecodePasses = 2;

    /// <summary>
    /// Formats decoded FT8 for the page. Each line shows strength, tone pitch, the message, and a plain-language reading.
    /// </summary>
    public static string Format(IReadOnlyList<Ft8Message> messages)
    {
        if (messages.Count == 0)
            return "";

        var lines = new List<string> { "FT8" };
        foreach (Ft8Message message in messages)
            lines.Add(FormatOne(message));
        return string.Join("\n", lines);
    }

    public static string FormatOne(Ft8Message message)
    {
        string head = $"{message.SnrDb,4:0} dB  {message.AudioHz,6:0} Hz  {message.Text}";
        string meaning = Explain(message.Text);
        return string.IsNullOrEmpty(meaning) ? head : $"{head}\n           {meaning}";
    }

    /// <summary>Plain-language reading of a standard FT8 line for beginners.</summary>
    public static string Explain(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        string[] parts = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return "";

        if (parts[0].Equals("CQ", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length >= 3 && parts[1].Equals("DX", StringComparison.OrdinalIgnoreCase))
                return $"{parts[2]} is calling for a long-distance (DX) contact.";
            if (parts.Length >= 3 && IsGrid(parts[^1]))
                return $"{parts[^2]} is calling anyone from {GridPhrase(parts[^1])}.";
            if (parts.Length >= 2)
                return $"{parts[^1]} is calling anyone (CQ).";
            return "A station is calling anyone (CQ).";
        }

        if (parts.Length == 2 && parts[0].Equals("TU", StringComparison.OrdinalIgnoreCase) && parts[1] == "73")
            return "Thank you, and goodbye (73).";

        if (parts.Length >= 3)
        {
            string a = parts[0];
            string b = parts[1];
            string rest = parts[2];
            if (rest is "RR73" or "RRR")
                return $"{b} confirms the exchange with {a} and is signing off.";
            if (rest == "73")
                return $"{b} is saying goodbye (73) to {a}.";
            if (IsReport(rest, out string report))
                return $"{b} is answering {a} with signal report {report} dB.";
            if (rest.StartsWith('R') && IsReport(rest[1..], out string confirmed))
                return $"{b} confirms to {a} with report {confirmed} dB.";
            if (IsGrid(rest))
                return $"{b} is working {a} and reports {GridPhrase(rest)}.";
        }

        if (parts.Length == 2)
            return $"{parts[1]} is addressing {parts[0]}.";

        return "Free-text or nonstandard FT8 message.";
    }

    private static string GridPhrase(string grid)
    {
        string? place = Maidenhead.Describe(grid);
        return place is null ? $"grid {grid}" : $"grid {grid} ({place})";
    }

    private static bool IsGrid(string token) =>
        token.Length is 4 or 6
        && char.IsLetter(token[0]) && char.IsLetter(token[1])
        && char.IsDigit(token[2]) && char.IsDigit(token[3]);

    private static bool IsReport(string token, out string report)
    {
        report = token;
        if (token.Length is < 2 or > 3)
            return false;
        if (token[0] is '+' or '-')
            return int.TryParse(token, out _);
        return false;
    }

    public static void Modulate(Span<float> audio, int sampleRate, string text, double audioHz, double startSeconds)
    {
        Span<byte> payload = stackalloc byte[10];
        Span<byte> tones = stackalloc byte[79];
        if (!Ft8Text.TryPack(text, payload))
            throw new ArgumentException($"Cannot pack FT8 message '{text}'.", nameof(text));

        Ft8Ldpc.EncodeTones(payload, tones);
        int samplesPerSymbol = (int)Math.Round(sampleRate * 0.16);
        int start = Math.Max(0, (int)Math.Round(startSeconds * sampleRate));
        double phase = 0;
        int cursor = start;
        foreach (byte tone in tones)
        {
            double frequency = audioHz + tone * 6.25;
            double step = 2 * Math.PI * frequency / sampleRate;
            for (int i = 0; i < samplesPerSymbol && cursor < audio.Length; i++, cursor++)
            {
                audio[cursor] += (float)Math.Cos(phase);
                phase += step;
                if (phase > Math.PI)
                    phase -= 2 * Math.PI;
            }
        }
    }

    public static IReadOnlyList<Ft8Message> Decode(ReadOnlySpan<float> audio, int sampleRate, Ft8Callsigns? calls = null) =>
        DecodeDetailed(audio, sampleRate, calls).Messages;

    /// <summary>Same as Decode, plus sync/LDPC counters for diagnosing empty slots.</summary>
    public static Ft8DecodeStats DecodeDetailed(ReadOnlySpan<float> audio, int sampleRate, Ft8Callsigns? calls = null)
    {
        if (audio.Length == 0 || sampleRate < 4_000)
            return new Ft8DecodeStats([], 0, 0, 0, 0);

        float[] wave = To12K(audio, sampleRate);
        if (wave.Length < SampleRate * 10)
            return new Ft8DecodeStats([], 0, 0, 0, wave.Length / (double)SampleRate);

        calls ??= Ft8Callsigns.Shared;
        Ft8DecodeStats best = DecodeWave(wave, calls);
        if (best.Messages.Count > 0)
            return best;

        // Rescue only when Costas almost locked — full ppm/Hilbert sweeps on noise are too slow for live listen.
        if (best.BestSyncScore >= 6f)
        {
            ReadOnlySpan<double> ppms = [-40, -20, 20, 40];
            ReadOnlySpan<double> shifts = [-25, -12.5, 12.5, 25];
            ReadOnlySpan<int> pads = [Hop / 2, Hop];
            foreach (double ppm in ppms)
            {
                best = Prefer(best, DecodeWave(StretchPpm(wave, ppm), calls));
                if (best.Messages.Count > 0)
                    return best;
            }

            foreach (double df in shifts)
            {
                best = Prefer(best, DecodeWave(HilbertShift(wave, df), calls));
                if (best.Messages.Count > 0)
                    return best;
            }

            foreach (int pad in pads)
            {
                best = Prefer(best, DecodeWave(PadFront(wave, pad), calls));
                if (best.Messages.Count > 0)
                    return best;
            }

            best = Prefer(best, DecodeWave(wave, calls, mirrorTones: true));
        }

        return best;
    }

    private static Ft8DecodeStats Prefer(Ft8DecodeStats a, Ft8DecodeStats b)
    {
        if (b.Messages.Count > a.Messages.Count)
            return b;
        if (b.Messages.Count == a.Messages.Count && b.LdpcAccepted > a.LdpcAccepted)
            return b;
        if (b.Messages.Count == a.Messages.Count && b.LdpcAccepted == a.LdpcAccepted && b.BestSyncScore > a.BestSyncScore)
            return b;
        return a;
    }

    private static Ft8DecodeStats DecodeWave(float[] wave, Ft8Callsigns calls, bool mirrorTones = false)
    {
        Waterfall waterfall = BuildWaterfall(wave);
        var packed = new List<Decoded>();
        var seenPayload = new HashSet<string>(StringComparer.Ordinal);
        int candidatesTried = 0;
        int ldpcPass = 0;
        float bestSync = 0;

        for (int pass = 0; pass < DecodePasses; pass++)
        {
            List<Candidate> candidates = FindCandidates(waterfall, mirrorTones);
            int foundThisPass = 0;
            foreach (Candidate rough in candidates)
            {
                Candidate candidate = RefineCandidate(waterfall, rough, mirrorTones);
                candidatesTried++;
                if (candidate.Score > bestSync)
                    bestSync = candidate.Score;
                if (!TryPayload(waterfall, candidate, out byte[] payload, mirrorTones))
                    continue;

                ldpcPass++;
                string key = Convert.ToHexString(payload);
                if (!seenPayload.Add(key))
                    continue;

                packed.Add(new Decoded(payload, candidate));
                waterfall.Blank(candidate);
                foundThisPass++;
            }

            if (foundThisPass == 0)
                break;
        }

        foreach (Decoded item in packed)
            Ft8Text.Unpack(item.Payload, calls);

        var messages = new List<Ft8Message>(packed.Count);
        foreach (Decoded item in packed)
            AddMessage(messages, item, calls);

        IReadOnlyList<Ft8Message> ordered = messages
            .OrderBy(message => message.TimeSeconds)
            .ThenBy(message => message.AudioHz)
            .ToList();
        return new Ft8DecodeStats(ordered, candidatesTried, ldpcPass, bestSync, wave.Length / (double)SampleRate);
    }

    private static float[] StretchPpm(float[] wave, double ppm)
    {
        double factor = 1.0 + ppm * 1e-6;
        int count = Math.Max(1, (int)(wave.Length / factor));
        var output = new float[count];
        for (int i = 0; i < count; i++)
        {
            double position = i * factor;
            int left = (int)position;
            int right = Math.Min(left + 1, wave.Length - 1);
            float mix = (float)(position - left);
            output[i] = wave[left] * (1 - mix) + wave[right] * mix;
        }

        return output;
    }

    /// <summary>Frequency-shift real audio via a cheap analytic (Hilbert) mix so Costas bins move intact.</summary>
    private static float[] HilbertShift(float[] wave, double shiftHz)
    {
        if (Math.Abs(shiftHz) < 1e-9)
            return wave;

        // FIR Hilbert (odd taps): delay real by (N-1)/2, imag is Hilbert.
        ReadOnlySpan<float> taps = [0.0025f, 0, 0.0124f, 0, 0.0375f, 0, 0.0834f, 0, 0.186f, 0, 0.627f, 0, -0.627f, 0, -0.186f, 0, -0.0834f, 0, -0.0375f, 0, -0.0124f, 0, -0.0025f];
        int n = taps.Length;
        int delay = n / 2;
        var output = new float[wave.Length];
        double step = 2 * Math.PI * shiftHz / SampleRate;
        double phase = 0;
        for (int i = 0; i < wave.Length; i++)
        {
            double imag = 0;
            for (int t = 0; t < n; t++)
            {
                int at = i - t;
                if ((uint)at < (uint)wave.Length)
                    imag += wave[at] * taps[t];
            }

            int reAt = i - delay;
            double real = (uint)reAt < (uint)wave.Length ? wave[reAt] : 0;
            output[i] = (float)(real * Math.Cos(phase) - imag * Math.Sin(phase));
            phase += step;
            if (phase > Math.PI)
                phase -= 2 * Math.PI;
            else if (phase < -Math.PI)
                phase += 2 * Math.PI;
        }

        return output;
    }

    private static float[] PadFront(float[] wave, int pad)
    {
        var output = new float[wave.Length + pad];
        Array.Copy(wave, 0, output, pad, wave.Length);
        return output;
    }

    private static void AddMessage(List<Ft8Message> messages, Decoded item, Ft8Callsigns calls)
    {
        string? text = Ft8Text.Unpack(item.Payload, calls);
        if (string.IsNullOrWhiteSpace(text))
            return;

        double hz = (item.Candidate.Bin + item.Candidate.FreqSub / (double)FreqOsr) / 0.16;
        double time = item.Candidate.Time * Hop / (double)SampleRate;
        var message = new Ft8Message(text, hz, time, item.Candidate.Score);
        // Only merge the same text at nearly the same tone — different stations a few dozen Hz apart stay separate.
        int index = messages.FindIndex(existing =>
            existing.Text == text
            && Math.Abs(existing.AudioHz - hz) < 50
            && Math.Abs(existing.TimeSeconds - time) < 0.5);
        if (index < 0)
            messages.Add(message);
        else if (message.SnrDb > messages[index].SnrDb)
            messages[index] = message;
    }

    private static bool TryPayload(Waterfall waterfall, Candidate candidate, out byte[] payload, bool mirrorTones = false)
    {
        payload = new byte[10];
        Span<float> llr = stackalloc float[Ft8Tables.LdpcN];
        Span<float> powers = stackalloc float[8];
        ReadOnlySpan<byte> gray = Ft8Tables.Gray;
        for (int k = 0; k < 58; k++)
        {
            int symbol = k + (k < 29 ? 7 : 14);
            int step = candidate.Time + symbol * TimeOsr;
            for (int j = 0; j < 8; j++)
            {
                int tone = mirrorTones ? 7 - gray[j] : gray[j];
                powers[j] = waterfall.At(step, candidate.FreqSub, candidate.Bin + tone);
            }

            llr[3 * k] = Max4(powers[4], powers[5], powers[6], powers[7]) - Max4(powers[0], powers[1], powers[2], powers[3]);
            llr[3 * k + 1] = Max4(powers[2], powers[3], powers[6], powers[7]) - Max4(powers[0], powers[1], powers[4], powers[5]);
            llr[3 * k + 2] = Max4(powers[1], powers[3], powers[5], powers[7]) - Max4(powers[0], powers[2], powers[4], powers[6]);
        }

        Normalize(llr);
        return Ft8Ldpc.TryDecode(llr, payload);
    }

    private static void Normalize(Span<float> llr)
    {
        double sum = 0;
        double sum2 = 0;
        foreach (float value in llr)
        {
            sum += value;
            sum2 += value * value;
        }

        double inv = 1.0 / llr.Length;
        double variance = (sum2 - sum * sum * inv) * inv;
        if (variance < 1e-6)
            return;
        float scale = (float)Math.Sqrt(24.0 / variance);
        for (int i = 0; i < llr.Length; i++)
            llr[i] *= scale;
    }

    private static List<Candidate> FindCandidates(Waterfall waterfall, bool mirrorTones = false)
    {
        int last = waterfall.Steps - 79 * TimeOsr;
        if (last < 0)
            return [];

        var ranked = new List<Candidate>();
        for (int freqSub = 0; freqSub < FreqOsr; freqSub++)
        {
            for (int bin = MinBin; bin <= MaxBin - 8; bin++)
            {
                for (int time = 0; time <= last; time++)
                {
                    float score = SyncScore(waterfall, time, freqSub, bin, mirrorTones);
                    if (score < MinSyncScore)
                        continue;
                    ranked.Add(new Candidate(time, freqSub, bin, score));
                }
            }
        }

        ranked.Sort(static (a, b) => b.Score.CompareTo(a.Score));
        var kept = new List<Candidate>();
        foreach (Candidate candidate in ranked)
        {
            bool near = kept.Exists(other =>
                Math.Abs(other.Time - candidate.Time) <= TimeOsr
                && Math.Abs(other.Bin - candidate.Bin) <= 1
                && other.FreqSub == candidate.FreqSub);
            if (near)
                continue;
            kept.Add(candidate);
            if (kept.Count == MaxCandidates)
                break;
        }

        return kept;
    }

    /// <summary>
    /// Polish a coarse Costas hit: search nearby time steps and tone bins before LDPC.
    /// On-air captures often sit between the coarse grid; unrefined LLRs then fail the CRC.
    /// </summary>
    private static Candidate RefineCandidate(Waterfall waterfall, Candidate rough, bool mirrorTones = false)
    {
        int last = waterfall.Steps - 79 * TimeOsr;
        Candidate best = rough;
        for (int dt = -TimeOsr; dt <= TimeOsr; dt++)
        {
            int time = rough.Time + dt;
            if (time < 0 || time > last)
                continue;
            for (int db = -1; db <= 1; db++)
            {
                int bin = rough.Bin + db;
                if (bin < MinBin || bin > MaxBin - 8)
                    continue;
                for (int freqSub = 0; freqSub < FreqOsr; freqSub++)
                {
                    float score = SyncScore(waterfall, time, freqSub, bin, mirrorTones);
                    if (score > best.Score)
                        best = new Candidate(time, freqSub, bin, score);
                }
            }
        }

        return best;
    }

    /// <summary>
    /// Costas sync quality: how far each sync tone sits above the other seven bins in that symbol.
    /// Uses the better of the full three-group average and the best single Costas group so a
    /// clipped slot edge still ranks the message.
    /// </summary>
    private static float SyncScore(Waterfall waterfall, int time, int freqSub, int bin, bool mirrorTones = false)
    {
        ReadOnlySpan<byte> costas = Ft8Tables.Costas;
        float total = 0;
        float bestGroup = float.NegativeInfinity;
        for (int group = 0; group < 3; group++)
        {
            float groupSum = 0;
            for (int k = 0; k < 7; k++)
            {
                int step = time + (group * 36 + k) * TimeOsr;
                int tone = mirrorTones ? 7 - costas[k] : costas[k];
                float peak = waterfall.At(step, freqSub, bin + tone);
                float others = 0;
                for (int j = 0; j < 8; j++)
                {
                    if (j == tone)
                        continue;
                    others += waterfall.At(step, freqSub, bin + j);
                }

                groupSum += peak - others / 7f;
            }

            groupSum /= 7f;
            total += groupSum;
            if (groupSum > bestGroup)
                bestGroup = groupSum;
        }

        float average = total / 3f;
        // Prefer a strong single Costas array when the slot was clipped, but keep the full average
        // as the usual score so random noise groups do not dominate.
        return Math.Max(average, 0.85f * bestGroup + 0.15f * average);
    }

    private static Waterfall BuildWaterfall(float[] wave)
    {
        int steps = Math.Max(0, (wave.Length - SymbolSamples) / Hop);
        var magnitude = new float[steps * FreqOsr * NumBins];
        var frame = new Complex[FftSize];
        float[] window = Filters.Hann(SymbolSamples);
        for (int step = 0; step < steps; step++)
        {
            Array.Clear(frame);
            int offset = step * Hop;
            for (int i = 0; i < SymbolSamples; i++)
                frame[i] = wave[offset + i] * window[i];

            Fourier.Forward(frame, FourierOptions.NoScaling);
            for (int freqSub = 0; freqSub < FreqOsr; freqSub++)
            {
                int dest = (step * FreqOsr + freqSub) * NumBins;
                for (int bin = 0; bin < NumBins; bin++)
                {
                    int src = (MinBin + bin) * FreqOsr + freqSub;
                    double power = frame[src].Real * frame[src].Real + frame[src].Imaginary * frame[src].Imaginary;
                    magnitude[dest + bin] = (float)(10 * Math.Log10(power + 1e-12));
                }
            }
        }

        return new Waterfall(magnitude, steps);
    }

    private static float[] To12K(ReadOnlySpan<float> audio, int sampleRate)
    {
        if (sampleRate == SampleRate)
            return audio.ToArray();

        if (sampleRate % SampleRate == 0)
        {
            int factor = sampleRate / SampleRate;
            int length = audio.Length / factor;
            var output = new float[length];
            for (int i = 0; i < length; i++)
            {
                float sum = 0;
                int baseIndex = i * factor;
                for (int k = 0; k < factor; k++)
                    sum += audio[baseIndex + k];
                output[i] = sum / factor;
            }

            return output;
        }

        int count = (int)((long)audio.Length * SampleRate / sampleRate);
        var resampled = new float[count];
        double ratio = (double)sampleRate / SampleRate;
        for (int i = 0; i < count; i++)
        {
            double position = i * ratio;
            int left = (int)position;
            int right = Math.Min(left + 1, audio.Length - 1);
            float mix = (float)(position - left);
            resampled[i] = audio[left] * (1 - mix) + audio[right] * mix;
        }

        return resampled;
    }

    private static float Max4(float a, float b, float c, float d) => Math.Max(Math.Max(a, b), Math.Max(c, d));

    /// <summary>The time-and-frequency picture FT8 is searched on. Each cell is the strength of one tone bin at one moment.</summary>
    private sealed class Waterfall(float[] magnitude, int steps)
    {
        public int Steps { get; } = steps;

        public float At(int step, int freqSub, int bin)
        {
            int index = bin - MinBin;
            if ((uint)step >= (uint)Steps || (uint)freqSub >= FreqOsr || (uint)index >= NumBins)
                return -120;
            return magnitude[(step * FreqOsr + freqSub) * NumBins + index];
        }

        /// <summary>Knock down a decoded signal so a later pass can hear weaker neighbors.</summary>
        public void Blank(Candidate candidate)
        {
            for (int symbol = 0; symbol < 79; symbol++)
            {
                int step = candidate.Time + symbol * TimeOsr;
                for (int j = 0; j < 8; j++)
                {
                    int index = candidate.Bin + j - MinBin;
                    if ((uint)step >= (uint)Steps || (uint)candidate.FreqSub >= FreqOsr || (uint)index >= NumBins)
                        continue;
                    magnitude[(step * FreqOsr + candidate.FreqSub) * NumBins + index] = -120;
                }
            }
        }
    }

    /// <summary>A possible FT8 signal: when it started, which frequency bin it occupies, and how well the sync tones matched.</summary>
    private readonly record struct Candidate(int Time, int FreqSub, int Bin, float Score);

    /// <summary>A candidate whose bits passed the error-correcting checks.</summary>
    private readonly record struct Decoded(byte[] Payload, Candidate Candidate);
}
