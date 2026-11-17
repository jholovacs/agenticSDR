using System.Numerics;
using MathNet.Numerics.IntegralTransforms;

namespace AgenticSdr.Dsp;

/// <summary>
/// One Ribbit / Rattlegram OFDM occupancy estimate in baseband audio.
/// Full polar-coded message text needs the native modem; this reports the 6.25 Hz comb.
/// </summary>
public sealed record RibbitHit(
    bool Active,
    double SnrDb,
    int CarrierHits,
    double CenterHz,
    double BandwidthHz,
    double NotchDb = 0);

/// <summary>
/// Identifies Ribbit (ORI) / Rattlegram OFDM in demodulated audio and formats occupancy lines.
/// Spec: ~256 carriers, 6.25 Hz spacing, ~1600 Hz occupied around ~1500 Hz audio, 48 kHz PCM.
/// </summary>
public static class RibbitDecoder
{
    public const double CarrierSpacingHz = 6.25;
    public const int CarrierCount = 256;
    public const double BandwidthHz = CarrierCount * CarrierSpacingHz;
    public const double CenterHz = 1_500;
    public const int SampleRate = 48_000;

    /// <summary>Low edge of the default OFDM block (carrier 0).</summary>
    public static double LowHz => CenterHz - BandwidthHz * 0.5;

    /// <summary>High edge of the default OFDM block.</summary>
    public static double HighHz => CenterHz + BandwidthHz * 0.5;

    public static string Decode(ReadOnlySpan<float> audio, int sampleRate)
    {
        RibbitHit hit = Detect(audio, sampleRate);
        return hit.Active ? Format(hit) : "";
    }

    public static string Format(RibbitHit hit)
    {
        if (!hit.Active)
            return "";
        return
            $"Ribbit OFDM · {hit.CarrierHits} carriers · {hit.SnrDb:0.0} dB · " +
            $"~{hit.CenterHz:0} Hz (±{hit.BandwidthHz * 0.5:0} Hz)";
    }

    /// <summary>
    /// Looks for a dense 6.25 Hz tone comb in the 700–2300 Hz Ribbit audio window.
    /// </summary>
    public static RibbitHit Detect(ReadOnlySpan<float> audio, int sampleRate)
    {
        // Need ≥32k FFT so 6.25 Hz carriers land in distinct bins (bin ≤ ~1.5 Hz at 48 kHz).
        if (audio.Length < 32_768 || sampleRate < 8_000)
            return Idle();

        int fftSize = SpectrumAnalyzer.PreviousPow2(Math.Min(audio.Length, sampleRate * 2));
        if (fftSize < 32_768)
            return Idle();

        double[] db = AudioPowerDb(audio, sampleRate, fftSize);
        double binHz = (double)sampleRate / fftSize;
        int lowBin = Math.Max(1, (int)Math.Floor(700 / binHz));
        int highBin = Math.Min(db.Length / 2 - 1, (int)Math.Ceiling(2_300 / binHz));
        if (highBin - lowBin < 32)
            return Idle();

        // Noise from sidebands outside the Ribbit window.
        var noise = new List<double>(64);
        int noiseLow = Math.Max(1, (int)Math.Floor(300 / binHz));
        int noiseHigh = Math.Min(db.Length / 2 - 1, (int)Math.Ceiling(3_500 / binHz));
        for (int i = noiseLow; i <= noiseHigh; i++)
        {
            double f = i * binHz;
            if (f >= 700 && f <= 2_300)
                continue;
            noise.Add(db[i]);
        }

        if (noise.Count < 16)
            return Idle();
        noise.Sort();
        double noiseFloor = noise[noise.Count / 4];
        double gate = noiseFloor + 6.0;

        // Score candidate grid offsets in [0, spacing) so a mistuned center still hits.
        // A true comb is hot on-grid and quieter halfway between carriers.
        int bestHits = 0;
        double bestSum = 0;
        double bestNotch = 0;
        double bestCenter = CenterHz;
        int steps = 8;
        for (int step = 0; step < steps; step++)
        {
            double offset = step * (CarrierSpacingHz / steps);
            int hits = 0;
            double sum = 0;
            double notchSum = 0;
            int notches = 0;
            double weighted = 0;
            double weight = 0;
            for (int c = 0; c < CarrierCount; c++)
            {
                double freq = LowHz + offset + c * CarrierSpacingHz;
                if (freq < 700 || freq > 2_300)
                    continue;
                int bin = (int)Math.Round(freq / binHz);
                if (bin < 1 || bin >= db.Length / 2)
                    continue;
                double power = db[bin];
                if (power < gate)
                    continue;
                hits++;
                sum += power - noiseFloor;
                weighted += freq * (power - noiseFloor);
                weight += power - noiseFloor;

                double midFreq = freq + CarrierSpacingHz * 0.5;
                int midBin = (int)Math.Round(midFreq / binHz);
                if (midBin > 0 && midBin < db.Length / 2)
                {
                    notchSum += power - db[midBin];
                    notches++;
                }
            }

            double notch = notches == 0 ? 0 : notchSum / notches;
            bool better = hits > bestHits
                || (hits == bestHits && sum > bestSum)
                || (hits == bestHits && Math.Abs(sum - bestSum) < 1 && notch > bestNotch);
            if (!better)
                continue;
            bestHits = hits;
            bestSum = sum;
            bestNotch = notch;
            bestCenter = weight > 0 ? weighted / weight : CenterHz + offset;
        }

        double snr = bestHits == 0 ? -40 : bestSum / bestHits;
        // Dense comb + mid-carrier notches: reject flat noise and single-tone speech.
        // Noise often lights many bins above a weak floor but lacks deep inter-carrier notches.
        bool active = bestHits >= 120 && snr >= 10.0 && bestNotch >= 5.0;
        return new RibbitHit(active, snr, bestHits, bestCenter, BandwidthHz, bestNotch);
    }

    /// <summary>
    /// Synthetic OFDM comb for detector tests (not a polar-coded Ribbit payload).
    /// </summary>
    public static float[] SynthesizeToneComb(
        int sampleRate = SampleRate,
        double durationSeconds = 0.8,
        double centerHz = CenterHz,
        double amplitude = 0.08)
    {
        int n = Math.Max(1, (int)(sampleRate * durationSeconds));
        var audio = new float[n];
        double low = centerHz - BandwidthHz * 0.5;
        for (int c = 0; c < CarrierCount; c++)
        {
            double freq = low + c * CarrierSpacingHz;
            double phase = (c * 0.37) % (2 * Math.PI);
            for (int i = 0; i < n; i++)
                audio[i] += (float)(amplitude * Math.Cos(2 * Math.PI * freq * i / sampleRate + phase));
        }

        // Soft fade so edges do not dominate the FFT.
        int fade = Math.Min(sampleRate / 50, n / 10);
        for (int i = 0; i < fade; i++)
        {
            float w = i / (float)fade;
            audio[i] *= w;
            audio[n - 1 - i] *= w;
        }

        return audio;
    }

    private static RibbitHit Idle() => new(false, -40, 0, CenterHz, BandwidthHz, 0);

    private static double[] AudioPowerDb(ReadOnlySpan<float> audio, int sampleRate, int fftSize)
    {
        int frames = Math.Max(1, audio.Length / fftSize);
        var acc = new double[fftSize];
        var frame = new Complex[fftSize];
        float[] window = Filters.Hann(fftSize);
        for (int h = 0; h < frames; h++)
        {
            int offset = h * fftSize;
            if (offset + fftSize > audio.Length)
                break;
            for (int i = 0; i < fftSize; i++)
                frame[i] = new Complex(audio[offset + i] * window[i], 0);
            Fourier.Forward(frame, FourierOptions.NoScaling);
            for (int i = 0; i < fftSize; i++)
            {
                double power = frame[i].Real * frame[i].Real + frame[i].Imaginary * frame[i].Imaginary;
                acc[i] += power;
            }
        }

        var db = new double[fftSize];
        for (int i = 0; i < fftSize; i++)
            db[i] = 10 * Math.Log10(acc[i] / frames + 1e-20);
        return db;
    }
}

/// <summary>
/// Streaming Ribbit occupancy reader: accumulates ~0.6 s of FM/SSB audio and emits when the OFDM comb appears.
/// </summary>
public sealed class RibbitReader
{
    private readonly List<float> _buffer = new(48_000);
    private int _sampleRate;
    private bool _wasActive;
    private int _sinceProbe;

    public RibbitReader(int initialSampleRate = RibbitDecoder.SampleRate)
    {
        _sampleRate = Math.Max(8_000, initialSampleRate);
    }

    public IReadOnlyList<string> Push(ReadOnlySpan<float> audio, int sampleRate)
    {
        if (audio.Length == 0 || sampleRate < 8_000)
            return [];

        if (sampleRate != _sampleRate)
        {
            _sampleRate = sampleRate;
            _buffer.Clear();
            _wasActive = false;
            _sinceProbe = 0;
        }

        _buffer.AddRange(audio);
        _sinceProbe += audio.Length;
        int keep = _sampleRate * 2;
        if (_buffer.Count > keep)
            _buffer.RemoveRange(0, _buffer.Count - keep);

        // Match RibbitDecoder's 32k FFT floor (about 0.7 s at 48 kHz).
        int need = Math.Max(32_768, _sampleRate * 3 / 4);
        // Probe about 2×/s once the buffer is warm — sample cadence, not wall clock.
        int probeEvery = Math.Max(_sampleRate / 2, 8_000);
        if (_buffer.Count < need || _sinceProbe < probeEvery)
            return [];
        _sinceProbe = 0;

        float[] window = _buffer.GetRange(_buffer.Count - need, need).ToArray();
        RibbitHit hit = RibbitDecoder.Detect(window, _sampleRate);
        if (!hit.Active)
        {
            _wasActive = false;
            return [];
        }

        if (_wasActive)
            return [];
        _wasActive = true;
        return [RibbitDecoder.Format(hit)];
    }
}
