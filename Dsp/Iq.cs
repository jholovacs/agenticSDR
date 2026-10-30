using System.Numerics;

namespace AgenticSdr.Dsp;

/// <summary>
/// Turns the radio's packed bytes into complex I/Q samples.
/// In direct sampling, if one of the two channels is much stronger, that channel is promoted with a Hilbert transform and marked as a possible image.
/// </summary>
public static class Iq
{
    public static (Complex[] Samples, bool RealValued) Convert(ReadOnlySpan<byte> raw, bool directSampling)
    {
        int n = raw.Length / 2;
        var iq = new Complex[n];
        Write(raw, iq);
        if (!directSampling || n == 0)
            return (iq, false);

        double iEnergy = 0;
        double qEnergy = 0;
        for (int i = 0; i < n; i++)
        {
            iEnergy += iq[i].Real * iq[i].Real;
            qEnergy += iq[i].Imaginary * iq[i].Imaginary;
        }

        bool iLive = iEnergy > qEnergy * 4 && iEnergy > n * 1e-6;
        bool qLive = qEnergy > iEnergy * 4 && qEnergy > n * 1e-6;
        if (!iLive && !qLive)
            return (iq, false);

        var real = new float[n];
        for (int i = 0; i < n; i++)
            real[i] = iLive ? (float)iq[i].Real : (float)iq[i].Imaginary;
        return (AnalyticSignal.Promote(real), true);
    }

    /// <summary>Writes unsigned 8-bit I/Q into an existing buffer so a live listen does not allocate a multi-megabyte array on every block.</summary>
    public static void Write(ReadOnlySpan<byte> raw, Span<Complex> destination)
    {
        int n = Math.Min(destination.Length, raw.Length / 2);
        for (int i = 0; i < n; i++)
        {
            float re = (raw[i * 2] - 127.5f) / 128f;
            float im = (raw[i * 2 + 1] - 127.5f) / 128f;
            destination[i] = new Complex(re, im);
        }
    }
}
