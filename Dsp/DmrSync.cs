using System.Numerics;

namespace AgenticSdr.Dsp;

/// <summary>
/// Recognizes DMR, a digital two-slot radio used on VHF and UHF.
/// It looks for the published 48-bit framing words from ETSI TS 102 361-1.
/// The voice inside those frames uses a proprietary codec, so this type does not turn the words into speech.
/// </summary>
public static class DmrSync
{
    public const string VoiceNote =
        "DMR framing found. The words ride on a proprietary voice codec, so they are not decoded. The audio is the digital signal itself.";

    private const int SymbolRate = 4_800;
    private const int AudioRate = 48_000;
    private const int SamplesPerSymbol = AudioRate / SymbolRate;
    private const double SymbolHz = 648;

    private static readonly int[][] Patterns =
    [
        Levels("755FD7DF75F7"),
        Levels("DFF57D75DF5D"),
        Levels("7F7D5DD57DFD"),
        Levels("D5D7F77FD757"),
        Levels("77D55F7DFD77"),
        Levels("5D577F7757FF"),
        Levels("F7FDD5DDFD55"),
        Levels("7DFFD5F55D5F"),
        Levels("D7557F5FF7F5")
    ];

    public static float[] SymbolStream(ReadOnlySpan<Complex> iq, double sampleRate, double offsetHz) =>
        Symbols(iq, sampleRate, offsetHz);

    public static bool Present(ReadOnlySpan<Complex> iq, double sampleRate, double offsetHz)
    {
        if (iq.Length < 256 || sampleRate < 8_000)
            return false;

        float[] symbols = Symbols(iq, sampleRate, offsetHz);
        if (symbols.Length < Patterns[0].Length + 4)
            return false;

        double best = 0;
        foreach (int[] pattern in Patterns)
        {
            for (int start = 0; start + pattern.Length <= symbols.Length; start++)
                best = Math.Max(best, Correlate(symbols.AsSpan(start, pattern.Length), pattern));
        }

        return best >= 0.72;
    }

    private static float[] Symbols(ReadOnlySpan<Complex> iq, double sampleRate, double offsetHz)
    {
        int factor = Math.Max(1, (int)Math.Round(sampleRate / AudioRate));
        int count = iq.Length / factor;
        if (count < SamplesPerSymbol * 30)
            return [];

        var disc = new float[count];
        double rotator = 0;
        double step = -2 * Math.PI * offsetHz / sampleRate;
        double accReal = 0;
        double accImag = 0;
        int got = 0;
        int written = 0;
        float previous = 0;
        bool havePhase = false;
        for (int i = 0; i < iq.Length; i++)
        {
            rotator += step;
            if (rotator > Math.PI)
                rotator -= 2 * Math.PI;
            else if (rotator < -Math.PI)
                rotator += 2 * Math.PI;
            double cosine = Math.Cos(rotator);
            double sine = Math.Sin(rotator);
            accReal += iq[i].Real * cosine - iq[i].Imaginary * sine;
            accImag += iq[i].Real * sine + iq[i].Imaginary * cosine;
            if (++got < factor)
                continue;

            float phase = (float)Math.Atan2(accImag, accReal);
            if (havePhase)
            {
                float turn = phase - previous;
                if (turn > MathF.PI)
                    turn -= 2 * MathF.PI;
                else if (turn < -MathF.PI)
                    turn += 2 * MathF.PI;
                disc[written] = turn;
            }

            previous = phase;
            havePhase = true;
            written++;
            accReal = 0;
            accImag = 0;
            got = 0;
        }

        if (written < SamplesPerSymbol * 30)
            return [];

        double bestScore = double.NegativeInfinity;
        float[] best = [];
        for (int phase = 0; phase < SamplesPerSymbol; phase++)
        {
            int length = (written - phase) / SamplesPerSymbol;
            if (length < Patterns[0].Length)
                continue;
            var symbols = new float[length];
            for (int i = 0; i < length; i++)
            {
                int at = phase + i * SamplesPerSymbol;
                float sum = 0;
                for (int k = 0; k < SamplesPerSymbol && at + k < written; k++)
                    sum += disc[at + k];
                symbols[i] = sum;
            }

            double energy = 0;
            foreach (float sample in symbols)
                energy += sample * sample;
            if (energy > bestScore)
            {
                bestScore = energy;
                best = symbols;
            }
        }

        return best;
    }

    private static double Correlate(ReadOnlySpan<float> observed, int[] pattern)
    {
        double mean = 0;
        foreach (float sample in observed)
            mean += sample;
        mean /= observed.Length;

        double dot = 0;
        double signal = 0;
        double reference = 0;
        for (int i = 0; i < pattern.Length; i++)
        {
            double centered = observed[i] - mean;
            dot += centered * pattern[i];
            signal += centered * centered;
            reference += pattern[i] * pattern[i];
        }

        if (signal < 1e-12 || reference < 1e-12)
            return 0;
        return dot / Math.Sqrt(signal * reference);
    }

    private static int[] Levels(string hex)
    {
        var bits = new List<int>(hex.Length * 4);
        foreach (char c in hex)
        {
            int value = Convert.ToInt32(c.ToString(), 16);
            for (int shift = 3; shift >= 0; shift--)
                bits.Add((value >> shift) & 1);
        }

        var levels = new int[bits.Count / 2];
        for (int i = 0; i < levels.Length; i++)
        {
            int dibit = (bits[i * 2] << 1) | bits[i * 2 + 1];
            levels[i] = dibit switch
            {
                0b01 => 3,
                0b00 => 1,
                0b10 => -1,
                _ => -3
            };
        }

        return levels;
    }
}
