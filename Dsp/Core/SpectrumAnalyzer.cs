using System.Numerics;
using MathNet.Numerics.IntegralTransforms;

namespace AgenticSdr.Dsp;

/// <summary>
/// Turns a block of I/Q into a power spectrum in decibels.
/// A Fourier transform splits the capture into frequency bins, a Hann window reduces smearing, and the result is shifted so the center frequency is in the middle.
/// </summary>
public static class SpectrumAnalyzer
{
    public static double[] AveragePowerDb(ReadOnlySpan<Complex> iq, int fftSize)
    {
        if (iq.Length < 16)
            throw new ArgumentException("Need more samples to form a spectrum.", nameof(iq));

        fftSize = Math.Min(fftSize, PreviousPow2(iq.Length));
        if (fftSize < 16)
            throw new ArgumentException("Need more samples to form a spectrum.", nameof(iq));

        int frames = iq.Length / fftSize;
        var acc = new double[fftSize];
        var frame = new Complex[fftSize];
        float[] window = Filters.Hann(fftSize);
        for (int h = 0; h < frames; h++)
        {
            int offset = h * fftSize;
            for (int i = 0; i < fftSize; i++)
                frame[i] = iq[offset + i] * window[i];

            Fourier.Forward(frame, FourierOptions.NoScaling);
            for (int i = 0; i < fftSize; i++)
            {
                int shifted = (i + fftSize / 2) % fftSize;
                double power = frame[i].Real * frame[i].Real + frame[i].Imaginary * frame[i].Imaginary;
                acc[shifted] += power;
            }
        }

        var db = new double[fftSize];
        for (int i = 0; i < fftSize; i++)
            db[i] = 10 * Math.Log10(acc[i] / frames + 1e-20);
        return db;
    }

    public static int PreviousPow2(int n)
    {
        int p = 1;
        while ((p << 1) > 0 && (p << 1) <= n)
            p <<= 1;
        return p;
    }
}
