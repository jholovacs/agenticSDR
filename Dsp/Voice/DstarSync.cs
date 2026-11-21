using System.Numerics;

namespace AgenticSdr.Dsp;

/// <summary>
/// Recognizes D-STAR DV: GMSK at 4800 bit/s with the JARL bit/frame sync words.
/// The AMBE voice codec is proprietary, so this type does not turn the words into speech.
/// </summary>
public static class DstarSync
{
    public const string VoiceNote =
        "D-STAR framing found. The words ride on a proprietary AMBE codec, so they are not decoded. The audio is the digital signal itself.";

    public const int BitRate = 4_800;
    public const int AudioRate = 48_000;
    public const int SamplesPerBit = AudioRate / BitRate;
    public const int HeaderCodedBits = 660;
    public const int FrameBits = 96;

    /// <summary>JARL 15-bit frame sync (transmission left→right).</summary>
    public static readonly int[] FrameSync = [1, 1, 1, 0, 1, 1, 0, 0, 1, 0, 1, 0, 0, 0, 0];

    /// <summary>Published 24-bit data-sync word in every 21st slow-data field (0x55 0x2D 0x16).</summary>
    public static readonly int[] DataSync = BytesToBits([0x55, 0x2D, 0x16]);

    public static bool Present(ReadOnlySpan<Complex> iq, double sampleRate, double offsetHz)
    {
        int[] bits = BitStream(iq, sampleRate, offsetHz);
        if (bits.Length < FrameSync.Length + 8)
            return false;
        return FindPattern(bits, FrameSync, maxErr: 1) >= 0
            || FindPattern(bits, Invert(FrameSync), maxErr: 1) >= 0
            || FindPattern(bits, DataSync, maxErr: 1) >= 0
            || FindPattern(bits, Invert(DataSync), maxErr: 1) >= 0;
    }

    /// <summary>Recover hard bits from FM-discriminator GMSK (both polarities tried by callers).</summary>
    public static int[] BitStream(ReadOnlySpan<Complex> iq, double sampleRate, double offsetHz)
    {
        if (iq.Length < 64 || sampleRate < 8_000)
            return [];

        int factor = Math.Max(1, (int)Math.Round(sampleRate / AudioRate));
        int count = iq.Length / factor;
        if (count < SamplesPerBit * 40)
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

        if (written < SamplesPerBit * 40)
            return [];

        // Light Gaussian-ish 3-tap average as a matched-filter stand-in.
        var filtered = new float[written];
        for (int i = 0; i < written; i++)
        {
            float a = i > 0 ? disc[i - 1] : disc[i];
            float b = disc[i];
            float c = i + 1 < written ? disc[i + 1] : disc[i];
            filtered[i] = 0.25f * a + 0.5f * b + 0.25f * c;
        }

        double bestEnergy = double.NegativeInfinity;
        int[] best = [];
        for (int phase = 0; phase < SamplesPerBit; phase++)
        {
            int length = (written - phase) / SamplesPerBit;
            if (length < FrameSync.Length)
                continue;
            var bits = new int[length];
            double energy = 0;
            for (int i = 0; i < length; i++)
            {
                int at = phase + i * SamplesPerBit;
                float sum = 0;
                for (int k = 0; k < SamplesPerBit && at + k < written; k++)
                    sum += filtered[at + k];
                bits[i] = sum >= 0 ? 1 : 0;
                energy += Math.Abs(sum);
            }

            if (energy > bestEnergy)
            {
                bestEnergy = energy;
                best = bits;
            }
        }

        return best;
    }

    public static int FindPattern(ReadOnlySpan<int> bits, ReadOnlySpan<int> pattern, int maxErr)
    {
        for (int i = 0; i + pattern.Length <= bits.Length; i++)
        {
            int err = 0;
            for (int k = 0; k < pattern.Length; k++)
            {
                if (bits[i + k] != pattern[k] && ++err > maxErr)
                    break;
            }

            if (err <= maxErr)
                return i;
        }

        return -1;
    }

    /// <summary>Build a synthetic DV IQ burst (header only) for tests.</summary>
    public static Complex[] ModulateHeaderIq(
        byte flag1,
        string rpt2,
        string rpt1,
        string ur,
        string my,
        string suffix,
        int sampleRate = AudioRate)
    {
        var bits = new List<int>(800);
        for (int i = 0; i < 32; i++)
        {
            bits.Add(1);
            bits.Add(0);
        }

        bits.AddRange(FrameSync);
        bits.AddRange(DstarReader.EncodeHeader(flag1, rpt2, rpt1, ur, my, suffix));
        return ModulateBits(bits, sampleRate);
    }

    public static Complex[] ModulateBits(IReadOnlyList<int> bits, int sampleRate = AudioRate)
    {
        int sps = Math.Max(4, (int)Math.Round(sampleRate / (double)BitRate));
        var iq = new Complex[bits.Count * sps];
        double phase = 0;
        // Binary CPFSK / MSK-ish: ±BitRate/4 Hz (h ≈ 0.5).
        double stepOne = 2 * Math.PI * (BitRate * 0.25) / sampleRate;
        double stepZero = -stepOne;
        int at = 0;
        foreach (int bit in bits)
        {
            double step = bit != 0 ? stepOne : stepZero;
            for (int n = 0; n < sps; n++)
            {
                phase += step;
                iq[at++] = new Complex(Math.Cos(phase), Math.Sin(phase));
            }
        }

        return iq;
    }

    public static int[] Invert(ReadOnlySpan<int> bits)
    {
        var inv = new int[bits.Length];
        for (int i = 0; i < bits.Length; i++)
            inv[i] = bits[i] ^ 1;
        return inv;
    }

    public static int[] BytesToBits(ReadOnlySpan<byte> bytes)
    {
        var bits = new int[bytes.Length * 8];
        for (int i = 0; i < bytes.Length; i++)
        {
            for (int b = 0; b < 8; b++)
                bits[i * 8 + b] = (bytes[i] >> (7 - b)) & 1;
        }

        return bits;
    }
}
