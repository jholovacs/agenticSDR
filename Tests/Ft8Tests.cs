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
    public void Crowded_slot_recovers_more_than_a_handful_of_signals()
    {
        var audio = new float[15 * Ft8Decoder.SampleRate];
        var expected = new List<string>();
        // More simultaneous tones than the old 24-candidate cap; spaced so Costas peaks stay distinct.
        for (int i = 0; i < 40; i++)
        {
            string text = $"CQ W{i % 10}XYZ FN{(40 + i % 10):00}";
            double hz = 400 + i * 55;
            Ft8Decoder.Modulate(audio, Ft8Decoder.SampleRate, text, hz, 0.5);
            expected.Add(text);
        }

        IReadOnlyList<Ft8Message> found = Ft8Decoder.Decode(audio, Ft8Decoder.SampleRate, new Ft8Callsigns());
        int hits = expected.Count(text => found.Any(message => message.Text == text));
        Assert.True(hits >= 30, $"expected at least 30 of 40 crowded signals, got {hits}");
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
    public void Ft8_survives_tuner_offset_iq_round_trip()
    {
        // Same LO placement as PlaceTuner: station 50 kHz below the tuned center.
        const double offset = Demodulator.VoiceOffsetHz;
        const double audioHz = 1_200;
        int count = (int)(15.2 * Demodulator.InputRate);
        var iq = new Complex[count];
        // Build 12 kHz audio, then upconvert into the IQ stream at (audioHz - offset).
        var audio = new float[(int)(15.2 * Ft8Decoder.SampleRate)];
        Ft8Decoder.Modulate(audio, Ft8Decoder.SampleRate, "CQ K1ABC FN42", audioHz, 0.5);
        double rfHz = audioHz - offset; // dial-relative tone relative to LO center
        int audioRate = Ft8Decoder.SampleRate;
        double upsample = Demodulator.InputRate / audioRate;
        double phase = 0;
        double step = 2 * Math.PI * rfHz / Demodulator.InputRate;
        for (int i = 0; i < count; i++)
        {
            int a = Math.Min(audio.Length - 1, (int)(i / upsample));
            double env = audio[a];
            iq[i] = new Complex(env * Math.Cos(phase), env * Math.Sin(phase));
            phase += step;
            if (phase > Math.PI)
                phase -= 2 * Math.PI;
            else if (phase < -Math.PI)
                phase += 2 * Math.PI;
        }

        DemodResult result = Demodulator.Demodulate(iq, RadioMode.Ft8, offsetHz: -offset, normalize: true);
        Assert.Equal(Ft8Decoder.SampleRate, result.SampleRate);
        IReadOnlyList<Ft8Message> found = Ft8Decoder.Decode(result.Audio, result.SampleRate, new Ft8Callsigns());
        Assert.Contains(found, message => message.Text == "CQ K1ABC FN42");
    }

    [Fact]
    public void Narrow_signal_on_an_ft8_dial_is_not_labeled_cw()
    {
        var peak = new SpectralPeak(14_075_200, -40, -60, 18, 250, 14_075_100, 14_075_350, 0, 0, false);
        var hint = new BandHint(RadioMode.Ft8, "20 m CW", "20 m FT8");
        Assert.Equal(RadioMode.Ft8, ModeClassifier.Classify(peak, hint).Mode);
    }

    [Theory]
    [InlineData("CQ K1ABC FN42", "K1ABC is calling anyone from grid FN42 (Boston).")]
    [InlineData("CQ DX K1ABC", "K1ABC is calling for a long-distance (DX) contact.")]
    [InlineData("K1ABC W9XYZ -12", "W9XYZ is answering K1ABC with signal report -12 dB.")]
    [InlineData("K1ABC W9XYZ R-05", "W9XYZ confirms to K1ABC with report -05 dB.")]
    [InlineData("K1ABC W9XYZ RR73", "W9XYZ confirms the exchange with K1ABC and is signing off.")]
    [InlineData("W9XYZ K1ABC 73", "K1ABC is saying goodbye (73) to W9XYZ.")]
    [InlineData("CQ XE1ABC EK09", "XE1ABC is calling anyone from grid EK09 (Mexico City).")]
    [InlineData("CQ BA1AA OM89", "BA1AA is calling anyone from grid OM89 (Beijing).")]
    [InlineData("CQ W2ABC FN13", "W2ABC is calling anyone from grid FN13 (upstate New York).")]
    public void Ft8_lines_are_explained_in_plain_language(string text, string meaning)
    {
        Assert.Equal(meaning, Ft8Decoder.Explain(text));
    }

    [Fact]
    public void Formatted_ft8_includes_message_and_meaning()
    {
        var messages = new[]
        {
            new Ft8Message("CQ K1ABC FN42", 1_500, 0.5, -12)
        };
        string formatted = Ft8Decoder.Format(messages);
        Assert.StartsWith("FT8", formatted, StringComparison.Ordinal);
        Assert.DoesNotContain("SNR =", formatted, StringComparison.Ordinal);
        Assert.Contains("CQ K1ABC FN42", formatted, StringComparison.Ordinal);
        Assert.Contains("calling anyone from grid FN42 (Boston)", formatted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("-12 dB", formatted, StringComparison.Ordinal);
        Assert.Contains("1500 Hz", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void A_comb_above_the_dial_is_ft8_activity()
    {
        const double dial = 28_074_000;
        const double center = 28_100_000;
        const double sampleRate = 240_000;
        const int n = 4096;
        double binHz = sampleRate / n;
        var db = Enumerable.Repeat(-60.0, n).ToArray();
        foreach (double audioHz in new[] { 400.0, 900.0, 1_400.0, 1_900.0, 2_400.0, 2_900.0 })
        {
            int bin = (int)Math.Round(((dial + audioHz) - center) / binHz + n / 2.0);
            db[bin] = -52;
            if (bin > 0) db[bin - 1] = -54;
            if (bin + 1 < n) db[bin + 1] = -54;
        }

        Ft8Score score = Ft8Activity.Score(db, center, sampleRate, dial);
        Assert.True(score.Active);
        Assert.True(score.Clusters >= 3);
        Assert.True(score.SnrDb >= 0);
    }

    [Fact]
    public void A_single_carrier_above_the_dial_is_not_ft8_activity()
    {
        const double dial = 28_074_000;
        const double center = 28_100_000;
        const double sampleRate = 240_000;
        const int n = 4096;
        double binHz = sampleRate / n;
        var db = Enumerable.Repeat(-60.0, n).ToArray();
        int bin = (int)Math.Round(((dial + 1_500) - center) / binHz + n / 2.0);
        db[bin] = -40;
        db[bin - 1] = -42;
        db[bin + 1] = -42;

        Ft8Score score = Ft8Activity.Score(db, center, sampleRate, dial);
        Assert.False(score.Active);
        Assert.True(score.Clusters <= 1);
    }

    [Fact]
    public void Empty_noise_above_the_dial_is_not_ft8_activity()
    {
        const double dial = 28_074_000;
        const double center = 28_100_000;
        const double sampleRate = 240_000;
        const int n = 4096;
        var db = Enumerable.Repeat(-60.0, n).ToArray();
        var random = new Random(3);
        for (int i = 0; i < n; i++)
            db[i] += random.NextDouble() - 0.5;

        Ft8Score score = Ft8Activity.Score(db, center, sampleRate, dial);
        Assert.False(score.Active);
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
