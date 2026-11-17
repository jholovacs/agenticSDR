using System.Numerics;

namespace AgenticSdr.Dsp;

/// <summary>
/// Turns the radio's packed bytes into complex I/Q samples.
/// In direct sampling, if one of the two channels is much stronger, that channel is promoted with a Hilbert transform and marked as a possible image.
/// </summary>
public static class Iq
{
    /// <summary>Keeps Hilbert FIR and I/Q branch choice across USB reads so digimode tones stay continuous.</summary>
    public sealed class DirectSamplingState
    {
        public AnalyticSignal.Stream Hilbert { get; } = new();
        public bool BranchLocked { get; set; }
        public bool UseI { get; set; } = true;
    }

    public static (Complex[] Samples, bool RealValued) Convert(ReadOnlySpan<byte> raw, bool directSampling, DirectSamplingState? state = null)
    {
        int n = raw.Length / 2;
        var iq = new Complex[n];
        bool realValued = Write(raw, iq, directSampling, state);
        return (iq, realValued);
    }

    /// <summary>Writes unsigned 8-bit I/Q into an existing buffer so a live listen does not allocate a multi-megabyte array on every block.</summary>
    public static void Write(ReadOnlySpan<byte> raw, Span<Complex> destination) =>
        Write(raw, destination, directSampling: false, state: null);

    public static bool Write(ReadOnlySpan<byte> raw, Span<Complex> destination, bool directSampling, DirectSamplingState? state)
    {
        int n = Math.Min(destination.Length, raw.Length / 2);
        for (int i = 0; i < n; i++)
        {
            float re = (raw[i * 2] - 127.5f) / 128f;
            float im = (raw[i * 2 + 1] - 127.5f) / 128f;
            destination[i] = new Complex(re, im);
        }

        if (!directSampling || n == 0)
            return false;

        bool useI = state?.UseI ?? true;
        if (state is null || !state.BranchLocked)
        {
            double iEnergy = 0;
            double qEnergy = 0;
            for (int i = 0; i < n; i++)
            {
                iEnergy += destination[i].Real * destination[i].Real;
                qEnergy += destination[i].Imaginary * destination[i].Imaginary;
            }

            bool iLive = iEnergy > qEnergy * 4 && iEnergy > n * 1e-6;
            bool qLive = qEnergy > iEnergy * 4 && qEnergy > n * 1e-6;
            if (!iLive && !qLive)
                return false;

            useI = iLive;
            if (state is not null)
            {
                state.UseI = useI;
                state.BranchLocked = true;
            }
        }

        var real = new float[n];
        for (int i = 0; i < n; i++)
            real[i] = useI ? (float)destination[i].Real : (float)destination[i].Imaginary;

        if (state is not null)
        {
            state.Hilbert.Promote(real, destination[..n]);
            return true;
        }

        Complex[] promoted = AnalyticSignal.Promote(real);
        promoted.AsSpan(0, n).CopyTo(destination);
        return true;
    }
}
