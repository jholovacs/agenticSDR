using System.Numerics;

namespace AgenticSdr.Dsp;

/// <summary>
/// Decides whether a peak is a station or a local spur.
/// Dongle clocks and nearby computers make steady whistles, often on a harmonic of 14.4 MHz.
/// A spur that slides when the radio is retuned is inside the receiver.
/// A carrier with no speech, no Morse keying, and no frequency swing is not voice or data, so the scan leaves it out.
/// A published channel is kept: a short capture can land between words.
/// An FT8 dial is kept: one FT8 tone holds still for a whole symbol and would otherwise look empty.
/// </summary>
public static class SignalContent
{
    /// <summary>RTL-SDR reference family. 28.8 MHz is the second harmonic of this clock.</summary>
    public const double ClockHz = 14_400_000;

    public static bool IsClockHarmonic(double frequencyHz, double toleranceHz = 3_000)
    {
        if (frequencyHz <= 0 || toleranceHz < 0)
            return false;
        double multiple = Math.Round(frequencyHz / ClockHz);
        if (multiple < 1)
            return false;
        return Math.Abs(frequencyHz - multiple * ClockHz) <= toleranceHz;
    }

    /// <summary>
    /// True when this peak should not be listed. It either moved with the tuner, or it is a narrow carrier with nothing on it.
    /// </summary>
    public static bool ShouldIgnore(
        double requestedHz,
        SpectralPeak peak,
        bool ft8Dial,
        ReadOnlySpan<Complex> iq,
        double sampleRate,
        double centerHz,
        bool knownChannel = false)
    {
        if (MovedWithTuner(requestedHz, peak.FrequencyHz, peak.BandwidthHz))
            return true;
        // A published channel is listed even when a short capture lands between words.
        if (ft8Dial || knownChannel || peak.BandwidthHz >= 2_500)
            return false;
        return !HasVoiceOrData(iq, sampleRate, peak.FrequencyHz - centerHz);
    }

    public static bool MovedWithTuner(double requestedHz, double measuredHz, double bandwidthHz)
    {
        double slack = bandwidthHz > 20_000 ? 60_000 : 12_000;
        return Math.Abs(measuredHz - requestedHz) > slack;
    }

    public static bool HasVoiceOrData(ReadOnlySpan<Complex> iq, double sampleRate, double offsetHz)
    {
        if (iq.Length < 256 || sampleRate < 8_000)
            return true;

        int factor = Math.Max(1, (int)Math.Round(sampleRate / 8_000.0));
        double audioRate = sampleRate / factor;
        const int envelopeFactor = 5;
        int audioCount = iq.Length / factor;
        if (audioCount < 64)
            return true;

        var envelope = new float[audioCount / envelopeFactor + 1];
        var delta = new float[audioCount];
        int written = 0;
        double rotator = 0;
        double step = -2 * Math.PI * offsetHz / sampleRate;
        double accReal = 0;
        double accImag = 0;
        int got = 0;
        float previous = 0;
        bool havePhase = false;
        int deltaCount = 0;
        double magnitudeSum = 0;
        int magnitudeCount = 0;

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

            double real = accReal / factor;
            double imag = accImag / factor;
            magnitudeSum += Math.Sqrt(real * real + imag * imag);
            if (++magnitudeCount == envelopeFactor)
            {
                envelope[written++] = (float)(magnitudeSum / envelopeFactor);
                magnitudeSum = 0;
                magnitudeCount = 0;
            }

            float phase = (float)Math.Atan2(imag, real);
            if (havePhase)
            {
                float turn = phase - previous;
                if (turn > MathF.PI)
                    turn -= 2 * MathF.PI;
                else if (turn < -MathF.PI)
                    turn += 2 * MathF.PI;
                delta[deltaCount++] = turn;
            }

            previous = phase;
            havePhase = true;
            accReal = 0;
            accImag = 0;
            got = 0;
        }

        int start = written / 5;
        if (written - start < 32)
            return true;
        ReadOnlySpan<float> loudness = envelope.AsSpan(start, written - start);
        double variation = EnvelopeVariation(loudness);
        int deltaStart = Math.Min(deltaCount / 5, deltaCount);
        double swingHz = FrequencySwingHz(delta.AsSpan(deltaStart, deltaCount - deltaStart), audioRate);
        // Morse goes quiet between marks. AM swings from soft to loud. A fuzzy carrier does neither.
        // FM keeps a steady loudness and moves the frequency.
        return LoudnessCarriesAudio(loudness, variation) || (variation < 0.15 && swingHz > 250);
    }

    private static bool LoudnessCarriesAudio(ReadOnlySpan<float> envelope, double variation)
    {
        if (variation < 0.28 || envelope.Length < 20)
            return false;
        var sorted = envelope.ToArray();
        Array.Sort(sorted);
        float quiet = sorted[sorted.Length / 5];
        float loud = sorted[sorted.Length * 4 / 5];
        if (loud < 1e-8)
            return false;
        // Keying spends real time near silence. A steady carrier with noise does not.
        if (quiet < loud * 0.25)
            return true;
        return loud > quiet * 2.2;
    }

    private static double EnvelopeVariation(ReadOnlySpan<float> envelope)
    {
        double sum = 0;
        double sumSquares = 0;
        foreach (float sample in envelope)
        {
            sum += sample;
            sumSquares += sample * sample;
        }

        double mean = sum / envelope.Length;
        if (mean < 1e-8)
            return 0;
        double variance = Math.Max(0, sumSquares / envelope.Length - mean * mean);
        return Math.Sqrt(variance) / mean;
    }

    private static double FrequencySwingHz(ReadOnlySpan<float> deltaPhase, double audioRate)
    {
        if (deltaPhase.Length < 32)
            return 0;
        double sum = 0;
        foreach (float sample in deltaPhase)
            sum += sample;
        double mean = sum / deltaPhase.Length;
        double squares = 0;
        foreach (float sample in deltaPhase)
        {
            double centered = sample - mean;
            squares += centered * centered;
        }

        return Math.Sqrt(squares / deltaPhase.Length) * audioRate / (2 * Math.PI);
    }
}
