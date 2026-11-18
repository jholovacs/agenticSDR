using System.Numerics;

namespace AgenticSdr.Dsp;

/// <summary>
/// Residual-carrier AFC for LEO downlinks (ISS). Estimates how far the FM packet/voice
/// sits from DC after the tuner shift, clamps to a Doppler window, and mixes it back to center.
/// </summary>
public sealed class DopplerFilter
{
    /// <summary>Typical ISS Doppler half-span on 2 m (~±3.5 kHz, with margin).</summary>
    public const double VhfHalfWindowHz = 5_000;

    /// <summary>Typical ISS Doppler half-span on 70 cm (~±10 kHz, with margin).</summary>
    public const double UhfHalfWindowHz = 12_000;

    private readonly double _maxHz;
    private readonly double _sampleRate;
    private Complex _mixOsc = Complex.One;
    private Complex _mixStep = Complex.One;
    private double _afcHz;
    private int _sinceEstimate;
    private readonly List<Complex> _probe = new(4_096);

    public DopplerFilter(double maxAbsOffsetHz, double basebandSampleRate)
    {
        _maxHz = Math.Max(0, maxAbsOffsetHz);
        _sampleRate = basebandSampleRate > 0 ? basebandSampleRate : 48_000;
        SetMix(0);
    }

    /// <summary>Smoothed residual offset currently being removed (Hz).</summary>
    public double OffsetHz => _afcHz;

    public static double HalfWindowHz(double frequencyHz) =>
        frequencyHz >= 400_000_000 ? UhfHalfWindowHz : VhfHalfWindowHz;

    /// <summary>
    /// Mix <paramref name="baseband"/> toward DC using the tracked Doppler offset.
    /// Updates the AFC from the same block every ~50 ms of audio.
    /// </summary>
    public void Process(List<Complex> baseband)
    {
        if (_maxHz <= 0 || baseband.Count == 0)
            return;

        _probe.AddRange(baseband);
        _sinceEstimate += baseband.Count;
        int need = Math.Max(512, (int)(_sampleRate * 0.05));
        if (_sinceEstimate >= need && _probe.Count >= 256)
        {
            double measured = EstimateOffsetHz(_probe, _sampleRate, _maxHz);
            // Slow loop so AFSK / voice deviation does not yank the LO.
            _afcHz = 0.85 * _afcHz + 0.15 * measured;
            _afcHz = Math.Clamp(_afcHz, -_maxHz, _maxHz);
            SetMix(-_afcHz);
            _sinceEstimate = 0;
            if (_probe.Count > need * 2)
                _probe.RemoveRange(0, _probe.Count - need);
        }

        Complex osc = _mixOsc;
        Complex step = _mixStep;
        for (int i = 0; i < baseband.Count; i++)
        {
            baseband[i] *= osc;
            osc *= step;
            if ((i & 1023) == 1023)
            {
                double inv = 1.0 / osc.Magnitude;
                osc = new Complex(osc.Real * inv, osc.Imaginary * inv);
            }
        }

        _mixOsc = osc;
    }

    /// <summary>
    /// Peak-bin residual frequency within ±maxHz of DC (positive = signal above DC).
    /// </summary>
    public static double EstimateOffsetHz(IReadOnlyList<Complex> samples, double sampleRate, double maxHz)
    {
        if (samples.Count < 64 || sampleRate <= 0 || maxHz <= 0)
            return 0;

        int n = SpectrumAnalyzer.PreviousPow2(Math.Min(samples.Count, 4_096));
        if (n < 64)
            return 0;

        var frame = new Complex[n];
        float[] window = Filters.Hann(n);
        int start = samples.Count - n;
        for (int i = 0; i < n; i++)
            frame[i] = samples[start + i] * window[i];

        MathNet.Numerics.IntegralTransforms.Fourier.Forward(frame, MathNet.Numerics.IntegralTransforms.FourierOptions.NoScaling);

        double binHz = sampleRate / n;
        int maxBin = Math.Min(n / 2 - 1, Math.Max(1, (int)Math.Ceiling(maxHz / binHz)));
        double bestPower = 0;
        int bestBin = 0;
        // DC ± maxBin (FFT not shifted — bin 0 is DC, high bins are negative freqs via wrap).
        for (int bin = 1; bin <= maxBin; bin++)
        {
            double pos = Power(frame[bin]);
            if (pos > bestPower)
            {
                bestPower = pos;
                bestBin = bin;
            }

            int neg = n - bin;
            double negPower = Power(frame[neg]);
            if (negPower > bestPower)
            {
                bestPower = negPower;
                bestBin = -bin;
            }
        }

        // Require the peak to stand out from nearby bins so noise does not park AFC off-center.
        double neighbor = 0;
        int count = 0;
        for (int bin = 1; bin <= maxBin; bin++)
        {
            if (bin == Math.Abs(bestBin))
                continue;
            neighbor += Power(frame[bin]) + Power(frame[n - bin]);
            count += 2;
        }

        double noise = count > 0 ? neighbor / count : bestPower;
        if (bestPower < noise * 2.5)
            return 0;

        return Math.Clamp(bestBin * binHz, -maxHz, maxHz);
    }

    private void SetMix(double offsetHz)
    {
        double dphi = 2 * Math.PI * offsetHz / _sampleRate;
        _mixStep = new Complex(Math.Cos(dphi), Math.Sin(dphi));
    }

    private static double Power(Complex c) => c.Real * c.Real + c.Imaginary * c.Imaginary;
}
