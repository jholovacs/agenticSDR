using System.Numerics;
using MathNet.Numerics.IntegralTransforms;

namespace AgenticSdr.Dsp;

/// <summary>
/// One decoded FT8 line. AudioHz is where the tone sat in the audio. TimeSeconds is when the 15-second slot started. SnrDb is the estimated strength.
/// </summary>
public sealed record Ft8Message(string Text, double AudioHz, double TimeSeconds, double SnrDb);

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

    public static string Format(IReadOnlyList<Ft8Message> messages)
    {
        if (messages.Count == 0)
            return "";
        return string.Join("\n", messages.Select(message =>
            $"{message.SnrDb,4:0} dB  {message.AudioHz,6:0} Hz  {message.Text}"));
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

    public static IReadOnlyList<Ft8Message> Decode(ReadOnlySpan<float> audio, int sampleRate, Ft8Callsigns? calls = null)
    {
        if (audio.Length == 0 || sampleRate < 4_000)
            return [];

        float[] wave = To12K(audio, sampleRate);
        if (wave.Length < SampleRate * 10)
            return [];

        Waterfall waterfall = BuildWaterfall(wave);
        List<Candidate> candidates = FindCandidates(waterfall);
        calls ??= Ft8Callsigns.Shared;
        var packed = new List<Decoded>();
        foreach (Candidate candidate in candidates)
        {
            if (!TryPayload(waterfall, candidate, out byte[] payload))
                continue;
            packed.Add(new Decoded(payload, candidate));
        }

        foreach (Decoded item in packed)
            Ft8Text.Unpack(item.Payload, calls);

        var messages = new List<Ft8Message>(packed.Count);
        foreach (Decoded item in packed)
            AddMessage(messages, item, calls);

        return messages
            .OrderBy(message => message.TimeSeconds)
            .ThenBy(message => message.AudioHz)
            .ToList();
    }

    private static void AddMessage(List<Ft8Message> messages, Decoded item, Ft8Callsigns calls)
    {
        string? text = Ft8Text.Unpack(item.Payload, calls);
        if (string.IsNullOrWhiteSpace(text))
            return;

        double hz = (item.Candidate.Bin + item.Candidate.FreqSub / (double)FreqOsr) / 0.16;
        double time = item.Candidate.Time * Hop / (double)SampleRate;
        var message = new Ft8Message(text, hz, time, item.Candidate.Score);
        int index = messages.FindIndex(existing => Math.Abs(existing.AudioHz - hz) < 20 && Math.Abs(existing.TimeSeconds - time) < 0.3);
        if (index < 0)
            messages.Add(message);
        else if (message.SnrDb > messages[index].SnrDb)
            messages[index] = message;
    }

    private static bool TryPayload(Waterfall waterfall, Candidate candidate, out byte[] payload)
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
                powers[j] = waterfall.At(step, candidate.FreqSub, candidate.Bin + gray[j]);

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

    private static List<Candidate> FindCandidates(Waterfall waterfall)
    {
        int last = waterfall.Steps - 79 * TimeOsr;
        var ranked = new List<Candidate>();
        for (int freqSub = 0; freqSub < FreqOsr; freqSub++)
        {
            for (int bin = MinBin; bin <= MaxBin - 8; bin++)
            {
                for (int time = 0; time <= last; time++)
                {
                    float score = SyncScore(waterfall, time, freqSub, bin);
                    if (score < 4)
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
            if (kept.Count == 24)
                break;
        }

        return kept;
    }

    private static float SyncScore(Waterfall waterfall, int time, int freqSub, int bin)
    {
        ReadOnlySpan<byte> costas = Ft8Tables.Costas;
        float sum = 0;
        int count = 0;
        for (int group = 0; group < 3; group++)
        {
            for (int k = 0; k < 7; k++)
            {
                int step = time + (group * 36 + k) * TimeOsr;
                int tone = costas[k];
                float power = waterfall.At(step, freqSub, bin + tone);
                if (tone > 0)
                {
                    sum += power - waterfall.At(step, freqSub, bin + tone - 1);
                    count++;
                }

                if (tone < 7)
                {
                    sum += power - waterfall.At(step, freqSub, bin + tone + 1);
                    count++;
                }
            }
        }

        return count == 0 ? 0 : sum / count;
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
    }

    /// <summary>A possible FT8 signal: when it started, which frequency bin it occupies, and how well the sync tones matched.</summary>
    private readonly record struct Candidate(int Time, int FreqSub, int Bin, float Score);

    /// <summary>A candidate whose bits passed the error-correcting checks.</summary>
    private readonly record struct Decoded(byte[] Payload, Candidate Candidate);
}
