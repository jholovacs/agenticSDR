using System.Numerics;
using AgenticSdr.Dsp;

namespace AgenticSdr.Tests;

/// <summary>Checks that a steady local carrier is ignored, and that Morse, AM voice, FM voice, and FT8 are kept.</summary>
public class SpurTests
{
    [Theory]
    [InlineData(14_400_000)]
    [InlineData(28_800_000)]
    [InlineData(144_000_000)]
    public void Clock_harmonics_are_recognized(double frequencyHz)
    {
        Assert.True(SignalContent.IsClockHarmonic(frequencyHz));
    }

    [Fact]
    public void A_calling_frequency_is_not_a_clock_harmonic()
    {
        Assert.False(SignalContent.IsClockHarmonic(146_520_000));
    }

    [Fact]
    public void A_steady_carrier_has_no_voice_or_data()
    {
        Complex[] iq = Carrier(seconds: 0.25, offsetHz: 0, modulate: null);
        Assert.False(SignalContent.HasVoiceOrData(iq, 48_000, 0));
    }

    [Fact]
    public void Noise_around_a_steady_carrier_is_not_voice()
    {
        const int rate = 48_000;
        int count = (int)(0.3 * rate);
        var iq = new Complex[count];
        var random = new Random(5);
        for (int i = 0; i < count; i++)
        {
            iq[i] = new Complex(1 + 0.55 * (random.NextDouble() * 2 - 1), 0.55 * (random.NextDouble() * 2 - 1));
        }

        Assert.False(SignalContent.HasVoiceOrData(iq, rate, 0));
    }

    [Fact]
    public void Amplitude_modulation_counts_as_voice()
    {
        Complex[] iq = Carrier(0.25, 0, i => 1 + 0.55 * Math.Sin(2 * Math.PI * 200 * i / 48_000));
        Assert.True(SignalContent.HasVoiceOrData(iq, 48_000, 0));
    }

    [Fact]
    public void Morse_keying_counts_as_data()
    {
        Complex[] iq = Carrier(0.4, 0, i => ((i / 48_000.0) % 0.08) < 0.04 ? 1 : 0);
        Assert.True(SignalContent.HasVoiceOrData(iq, 48_000, 0));
    }

    [Fact]
    public void Frequency_modulation_counts_as_voice()
    {
        const int rate = 48_000;
        int count = (int)(0.25 * rate);
        var iq = new Complex[count];
        double phase = 0;
        double modulator = 0;
        double modStep = 2 * Math.PI * 1_000 / rate;
        double deviationStep = 2 * Math.PI * 500 / rate;
        for (int i = 0; i < count; i++)
        {
            modulator += modStep;
            phase += deviationStep * Math.Sin(modulator);
            iq[i] = new Complex(Math.Cos(phase), Math.Sin(phase));
        }

        Assert.True(SignalContent.HasVoiceOrData(iq, rate, 0));
    }

    [Fact]
    public void A_carrier_that_slides_with_the_tuner_is_ignored()
    {
        var peak = new SpectralPeak(100_050_000, -20, -80, 30, 600, 100_049_000, 100_051_000, 20, 0, false);
        Assert.True(SignalContent.MovedWithTuner(100_000_000, peak.FrequencyHz, peak.BandwidthHz));
        Assert.True(SignalContent.ShouldIgnore(100_000_000, peak, ft8Dial: false, [], 48_000, 100_050_000));
    }

    [Fact]
    public void A_named_channel_is_kept_when_the_capture_is_quiet()
    {
        Complex[] iq = Carrier(0.25, 0, null);
        var peak = new SpectralPeak(162_425_000, -20, -40, 12, 600, 162_424_700, 162_425_300, 8, 0, false);
        Assert.True(SignalContent.ShouldIgnore(162_425_000, peak, ft8Dial: false, iq, 48_000, 162_475_000));
        Assert.False(SignalContent.ShouldIgnore(162_425_000, peak, ft8Dial: false, iq, 48_000, 162_475_000, knownChannel: true));
    }

    [Fact]
    public void An_ft8_dial_is_kept_even_when_the_tone_is_steady()
    {
        Complex[] iq = Carrier(0.25, 0, null);
        var peak = new SpectralPeak(14_074_000, -20, -80, 20, 400, 14_073_800, 14_074_200, 15, 0, false);
        Assert.False(SignalContent.ShouldIgnore(14_074_000, peak, ft8Dial: true, iq, 48_000, 14_074_000));
    }

    private static Complex[] Carrier(double seconds, double offsetHz, Func<int, double>? modulate)
    {
        const int rate = 48_000;
        int count = (int)(seconds * rate);
        var iq = new Complex[count];
        double phase = 0;
        double step = 2 * Math.PI * offsetHz / rate;
        for (int i = 0; i < count; i++)
        {
            phase += step;
            double scale = modulate?.Invoke(i) ?? 1;
            iq[i] = new Complex(scale * Math.Cos(phase), scale * Math.Sin(phase));
        }

        return iq;
    }
}
