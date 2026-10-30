using System.Numerics;
using AgenticSdr.Dsp;

namespace AgenticSdr.Tests;

/// <summary>Checks FT8 pack and unpack, a noisy audio round trip, and that a signal on an FT8 dial is not labeled Morse.</summary>
public class Ft8Tests
{
    [Theory]
    [InlineData("CQ K1ABC FN42")]
    [InlineData("K1ABC W9XYZ -12")]
    [InlineData("K1ABC W9XYZ R-05")]
    [InlineData("K1ABC W9XYZ RR73")]
    [InlineData("CQ DX K1ABC")]
    [InlineData("W9XYZ K1ABC 73")]
    [InlineData("TU 73")]
    public void Messages_pack_and_unpack(string text)
    {
        Span<byte> payload = stackalloc byte[10];
        Assert.True(Ft8Text.TryPack(text, payload));
        Assert.Equal(text, Ft8Text.Unpack(payload, new Ft8Callsigns()));
    }

    [Fact]
    public void Audio_round_trip_finds_two_signals()
    {
        var audio = new float[15 * Ft8Decoder.SampleRate];
        Ft8Decoder.Modulate(audio, Ft8Decoder.SampleRate, "CQ K1ABC FN42", 1_500, 0.5);
        Ft8Decoder.Modulate(audio, Ft8Decoder.SampleRate, "K1ABC W9XYZ -12", 800, 0.5);

        IReadOnlyList<Ft8Message> found = Ft8Decoder.Decode(audio, Ft8Decoder.SampleRate, new Ft8Callsigns());

        Assert.Contains(found, message => message.Text == "CQ K1ABC FN42");
        Assert.Contains(found, message => message.Text == "K1ABC W9XYZ -12");
        Ft8Message cq = found.First(message => message.Text == "CQ K1ABC FN42");
        Assert.InRange(cq.AudioHz, 1_490, 1_510);
    }

    [Fact]
    public void Audio_round_trip_survives_moderate_noise()
    {
        var audio = new float[15 * Ft8Decoder.SampleRate];
        Ft8Decoder.Modulate(audio, Ft8Decoder.SampleRate, "CQ K1ABC FN42", 1_234, 0.48);
        var random = new Random(7);
        const float sigma = 2f;
        for (int i = 0; i < audio.Length; i++)
        {
            double u1 = 1 - random.NextDouble();
            double u2 = 1 - random.NextDouble();
            audio[i] += sigma * (float)(Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2));
        }

        IReadOnlyList<Ft8Message> found = Ft8Decoder.Decode(audio, Ft8Decoder.SampleRate, new Ft8Callsigns());
        Assert.Contains(found, message => message.Text == "CQ K1ABC FN42");
    }

    [Fact]
    public void Ft8_demod_keeps_the_upper_sideband_tone()
    {
        int count = (int)(0.25 * Demodulator.InputRate);
        var iq = new Complex[count];
        double phase = 0;
        double step = 2 * Math.PI * 1_500 / Demodulator.InputRate;
        for (int i = 0; i < count; i++)
        {
            iq[i] = new Complex(Math.Cos(phase), Math.Sin(phase));
            phase += step;
            if (phase > Math.PI)
                phase -= 2 * Math.PI;
        }

        DemodResult result = Demodulator.Demodulate(iq, RadioMode.Ft8, normalize: false);
        Assert.Equal(Ft8Decoder.SampleRate, result.SampleRate);
        Assert.True(result.Audio.Length > 1_000);
        double tone = Goertzel(result.Audio, result.SampleRate, 1_500);
        double other = Goertzel(result.Audio, result.SampleRate, 500);
        Assert.True(tone > other * 10);
    }

    [Fact]
    public void Narrow_signal_on_an_ft8_dial_is_not_labeled_cw()
    {
        var peak = new SpectralPeak(14_075_200, -40, -60, 18, 250, 14_075_100, 14_075_350, 0, 0, false);
        var hint = new BandHint(RadioMode.Ft8, "20 m CW", "20 m FT8");
        Assert.Equal(RadioMode.Ft8, ModeClassifier.Classify(peak, hint).Mode);
    }

    private static double Goertzel(float[] audio, int sampleRate, double frequency)
    {
        double real = 0;
        double imag = 0;
        for (int i = 0; i < audio.Length; i++)
        {
            double angle = 2 * Math.PI * frequency * i / sampleRate;
            real += audio[i] * Math.Cos(angle);
            imag += audio[i] * Math.Sin(angle);
        }

        return real * real + imag * imag;
    }
}
