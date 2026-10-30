using System.Numerics;

namespace AgenticSdr.Dsp;

/// <summary>
/// Builds a complex signal from one real channel by delaying that channel and adding a Hilbert (90-degree) companion.
/// Used when direct sampling gives only one usable wire of the antenna input.
/// </summary>
public static class AnalyticSignal
{
    public static Complex[] Promote(ReadOnlySpan<float> real)
    {
        float[] hilbert = Filters.Hilbert(63);
        int delay = hilbert.Length / 2;
        var iq = new Complex[real.Length];
        for (int n = 0; n < real.Length; n++)
        {
            double imag = 0;
            int taps = Math.Min(hilbert.Length, n + 1);
            for (int k = 0; k < taps; k++)
                imag += hilbert[k] * real[n - k];

            int delayed = n - delay;
            float re = delayed >= 0 ? real[delayed] : 0;
            iq[n] = new Complex(re, imag);
        }

        return iq;
    }
}
