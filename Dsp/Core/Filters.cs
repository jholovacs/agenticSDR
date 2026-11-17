namespace AgenticSdr.Dsp;

/// <summary>
/// Small FIR filters. A lowpass keeps frequencies below a cutoff. A Hilbert transformer builds the missing half of a real-valued signal.
/// The window is a Hann or Hamming taper so the filter does not ring.
/// </summary>
public static class Filters
{
    public static float[] Lowpass(int taps, double cutoffHz, double sampleRate)
    {
        if (taps < 3)
            throw new ArgumentOutOfRangeException(nameof(taps));
        if (taps % 2 == 0)
            taps++;

        double fc = cutoffHz / sampleRate;
        var h = new float[taps];
        int mid = taps / 2;
        double sum = 0;
        for (int n = 0; n < taps; n++)
        {
            int k = n - mid;
            double sinc = k == 0
                ? 2 * fc
                : Math.Sin(2 * Math.PI * fc * k) / (Math.PI * k);
            double window = 0.54 - 0.46 * Math.Cos(2 * Math.PI * n / (taps - 1));
            h[n] = (float)(sinc * window);
            sum += h[n];
        }

        if (sum != 0)
        {
            for (int n = 0; n < taps; n++)
                h[n] /= (float)sum;
        }

        return h;
    }

    /// <summary>Bandpass via lowpass(high) − lowpass(low). Odd tap count.</summary>
    public static float[] Bandpass(int taps, double lowHz, double highHz, double sampleRate)
    {
        if (lowHz >= highHz)
            throw new ArgumentOutOfRangeException(nameof(lowHz));
        float[] high = Lowpass(taps, highHz, sampleRate);
        float[] low = Lowpass(taps, lowHz, sampleRate);
        var h = new float[high.Length];
        for (int i = 0; i < h.Length; i++)
            h[i] = high[i] - low[i];
        return h;
    }

    public static float[] Hilbert(int taps)
    {
        if (taps % 2 == 0)
            taps++;
        var h = new float[taps];
        int mid = taps / 2;
        for (int n = 0; n < taps; n++)
        {
            int k = n - mid;
            if (k == 0 || (k & 1) == 0)
                continue;
            double window = 0.54 - 0.46 * Math.Cos(2 * Math.PI * n / (taps - 1));
            h[n] = (float)(2.0 / (Math.PI * k) * window);
        }

        return h;
    }

    public static float[] Hann(int n)
    {
        var w = new float[n];
        if (n == 1)
        {
            w[0] = 1;
            return w;
        }

        for (int i = 0; i < n; i++)
            w[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1)));
        return w;
    }
}
