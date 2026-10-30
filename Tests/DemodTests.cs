using System.Diagnostics;
using System.Numerics;
using AgenticSdr.Dsp;

namespace AgenticSdr.Tests;

/// <summary>Checks that FM, wide FM, and AM demodulation keep the audio that was put in and land near 48 kHz.</summary>
public class DemodTests
{
    [Fact]
    public void Unmodulated_fm_is_quiet_and_the_clip_has_the_right_length()
    {
        const double seconds = 0.2;
        Complex[] iq = Tone(seconds, offsetHz: 25_000, envelope: null);
        DemodResult result = Demodulator.Demodulate(iq, RadioMode.Nbfm, offsetHz: 25_000, normalize: false);

        int expected = (int)(seconds * Demodulator.OutputRate);
        Assert.InRange(result.Audio.Length, (int)(expected * 0.8), (int)(expected * 1.05));
        Assert.Equal(Demodulator.OutputRate, result.SampleRate);
        Assert.True(Rms(result.Audio) < 0.05, $"RMS was {Rms(result.Audio)}");
    }

    [Fact]
    public void Wide_fm_keeps_a_1_kilohertz_tone()
    {
        const double seconds = 0.12;
        const double deviation = 15_000;
        const double tone = 1_000;
        int count = (int)(seconds * Demodulator.InputRate);
        var iq = new Complex[count];
        double phase = 0;
        double modulator = 0;
        double modStep = 2 * Math.PI * tone / Demodulator.InputRate;
        double deviationStep = 2 * Math.PI * deviation / Demodulator.InputRate;
        for (int i = 0; i < count; i++)
        {
            modulator += modStep;
            phase += deviationStep * Math.Sin(modulator);
            iq[i] = new Complex(Math.Cos(phase), Math.Sin(phase));
        }

        DemodResult result = Demodulator.Demodulate(iq, RadioMode.Wbfm, normalize: false);
        Assert.Equal(Demodulator.OutputRate, result.SampleRate);
        double atTone = Correlate(result.Audio, result.SampleRate, tone);
        double atOther = Correlate(result.Audio, result.SampleRate, 3_000);
        Assert.True(atTone > atOther * 3, $"1 kHz {atTone}, 3 kHz {atOther}");
    }

    [Fact]
    public void Wide_fm_keeps_a_loud_broadcast_deviation_off_center()
    {
        const double seconds = 0.12;
        const double deviation = 60_000;
        const double tone = 1_000;
        const double place = -Demodulator.WideFmOffsetHz;
        int count = (int)(seconds * Demodulator.InputRate);
        var iq = new Complex[count];
        double phase = 0;
        double modulator = 0;
        double carrier = 2 * Math.PI * place / Demodulator.InputRate;
        double modStep = 2 * Math.PI * tone / Demodulator.InputRate;
        double deviationStep = 2 * Math.PI * deviation / Demodulator.InputRate;
        for (int i = 0; i < count; i++)
        {
            modulator += modStep;
            phase += carrier + deviationStep * Math.Sin(modulator);
            iq[i] = new Complex(Math.Cos(phase), Math.Sin(phase));
        }

        DemodResult result = Demodulator.Demodulate(iq, RadioMode.Wbfm, offsetHz: place, normalize: false);
        double atTone = Correlate(result.Audio, result.SampleRate, tone);
        double atOther = Correlate(result.Audio, result.SampleRate, 3_000);
        Assert.True(atTone > atOther * 3, $"1 kHz {atTone}, 3 kHz {atOther}");
    }

    [Fact]
    public void Wide_fm_keeps_up_with_the_dongle()
    {
        const double seconds = 0.5;
        int count = (int)(seconds * Demodulator.InputRate);
        var iq = new Complex[count];
        double step = 2 * Math.PI * -Demodulator.WideFmOffsetHz / Demodulator.InputRate;
        double phase = 0;
        for (int i = 0; i < count; i++)
        {
            phase += step;
            iq[i] = new Complex(Math.Cos(phase), Math.Sin(phase));
        }

        var demod = new Demodulator(RadioMode.Wbfm, -Demodulator.WideFmOffsetHz);
        var clock = Stopwatch.StartNew();
        const int chunk = 131_072;
        for (int offset = 0; offset < iq.Length; offset += chunk)
            demod.Push(iq.AsSpan(offset, Math.Min(chunk, iq.Length - offset)));
        clock.Stop();
        Assert.True(
            clock.Elapsed.TotalSeconds < seconds * 0.5,
            $"Wide FM took {clock.Elapsed.TotalSeconds:0.00} s to demodulate {seconds:0.00} s. The dongle would drop samples, and each drop is a click.");
    }

    [Fact]
    public void Am_demod_keeps_the_modulation_tone()
    {
        const double seconds = 0.25;
        Complex[] iq = Tone(seconds, offsetHz: 0, i => 1 + 0.5 * Math.Sin(2 * Math.PI * 1_000 * i / Demodulator.InputRate));
        DemodResult result = Demodulator.Demodulate(iq, RadioMode.Am, normalize: false);
        double atTone = Correlate(result.Audio, result.SampleRate, 1_000);
        double atOther = Correlate(result.Audio, result.SampleRate, 3_000);
        Assert.True(atTone > atOther * 3, $"1 kHz {atTone}, 3 kHz {atOther}");
    }

    [Fact]
    public void Audio_can_be_taken_as_it_is_produced()
    {
        Complex[] iq = Tone(0.2, offsetHz: 0, envelope: null);
        var demod = new Demodulator(RadioMode.Am);
        int half = iq.Length / 2;
        demod.Push(iq.AsSpan(0, half));
        float[] first = demod.TakeNewAudio();
        demod.Push(iq.AsSpan(half));
        float[] second = demod.TakeNewAudio();
        Assert.NotEmpty(first);
        Assert.NotEmpty(second);
        Assert.Empty(demod.TakeNewAudio());
        Assert.Equal(Demodulator.OutputRate, demod.PlaybackRate);
    }

    private static Complex[] Tone(double seconds, double offsetHz, Func<int, double>? envelope)
    {
        int n = (int)(seconds * Demodulator.InputRate);
        var iq = new Complex[n];
        double phase = 0;
        double step = 2 * Math.PI * offsetHz / Demodulator.InputRate;
        for (int i = 0; i < n; i++)
        {
            phase += step;
            double env = envelope?.Invoke(i) ?? 1;
            iq[i] = new Complex(env * Math.Cos(phase), env * Math.Sin(phase));
        }

        return iq;
    }

    private static double Rms(float[] audio)
    {
        int start = Math.Min(audio.Length - 1, (int)(0.05 * Demodulator.OutputRate));
        double sum = 0;
        int count = 0;
        for (int i = start; i < audio.Length; i++)
        {
            sum += audio[i] * audio[i];
            count++;
        }

        return count == 0 ? 0 : Math.Sqrt(sum / count);
    }

    private static double Correlate(float[] audio, int sampleRate, double frequency)
    {
        int start = audio.Length / 5;
        double iSum = 0;
        double qSum = 0;
        int count = 0;
        for (int i = start; i < audio.Length; i++)
        {
            double phase = 2 * Math.PI * frequency * i / sampleRate;
            iSum += audio[i] * Math.Cos(phase);
            qSum += audio[i] * Math.Sin(phase);
            count++;
        }

        return Math.Sqrt(iSum * iSum + qSum * qSum) / Math.Max(1, count);
    }
}
