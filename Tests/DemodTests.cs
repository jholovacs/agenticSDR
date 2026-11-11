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
        Assert.InRange(result.SampleRate, 47_000, 51_000);
        Assert.Equal(2, result.Channels);
        double secondsOut = result.Audio.Length / (double)(result.Channels * result.SampleRate);
        Assert.InRange(secondsOut, seconds * 0.85, seconds * 1.15);
        float[] mono = Mono(result.Audio, result.Channels);
        double atTone = Correlate(mono, result.SampleRate, tone);
        double atOther = Correlate(mono, result.SampleRate, 3_000);
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
        float[] mono = Mono(result.Audio, result.Channels);
        double atTone = Correlate(mono, result.SampleRate, tone);
        double atOther = Correlate(mono, result.SampleRate, 3_000);
        Assert.True(atTone > atOther * 3, $"1 kHz {atTone}, 3 kHz {atOther}");
    }

    [Fact]
    public void Wide_fm_multiplex_separates_left_and_right()
    {
        // Drive the multiplex decoder directly so the check is about stereo matrixing, not the FM front-end.
        const double mpxRate = 400_000;
        const int audioRate = 48_000;
        const double seconds = 1.5; // pilot bandpass + PLL need a moment to lock
        const double leftTone = 900;
        const double rightTone = 1_700;
        int count = (int)(seconds * mpxRate);
        var mpx = new float[count];
        double leftPhase = 0;
        double rightPhase = 0;
        double pilotPhase = 0;
        double leftStep = 2 * Math.PI * leftTone / mpxRate;
        double rightStep = 2 * Math.PI * rightTone / mpxRate;
        double pilotStep = 2 * Math.PI * 19_000 / mpxRate;
        for (int i = 0; i < count; i++)
        {
            leftPhase += leftStep;
            rightPhase += rightStep;
            pilotPhase += pilotStep;
            double l = Math.Sin(leftPhase);
            double r = Math.Sin(rightPhase);
            double sum = 0.5 * (l + r);
            double diff = 0.5 * (l - r);
            mpx[i] = (float)(sum + 0.15 * Math.Sin(pilotPhase) + diff * Math.Sin(2 * pilotPhase));
        }

        var mux = new WbfmMux(mpxRate, audioRate);
        var audio = new List<float>();
        const int chunk = 8_192;
        for (int offset = 0; offset < mpx.Length; offset += chunk)
            mux.Process(mpx.AsSpan(offset, Math.Min(chunk, mpx.Length - offset)), audio);

        Assert.Equal(2, mux.Channels);
        Assert.True(audio.Count > 2_000, $"Expected stereo audio, got {audio.Count} samples");
        float[] left = Channel(audio.ToArray(), 0, 2);
        float[] right = Channel(audio.ToArray(), 1, 2);
        int skip = left.Length / 3;
        float[] leftTail = left[skip..];
        float[] rightTail = right[skip..];
        // Effective rate after decimation Round(mpx/audio).
        int outRate = (int)Math.Round(mpxRate / Math.Round(mpxRate / audioRate));
        double rms = Math.Sqrt(leftTail.Sum(s => s * s) / leftTail.Length);
        double leftAtLeft = Correlate(leftTail, outRate, leftTone);
        double leftAtRight = Correlate(leftTail, outRate, rightTone);
        double rightAtRight = Correlate(rightTail, outRate, rightTone);
        double rightAtLeft = Correlate(rightTail, outRate, leftTone);
        Assert.True(rms > 0.01, $"Audio too quiet RMS={rms}; L900={leftAtLeft} L1700={leftAtRight} stereo={mux.Stereo}");
        Assert.True(leftAtLeft > leftAtRight * 2, $"L: {leftTone}={leftAtLeft}, {rightTone}={leftAtRight}; stereo={mux.Stereo}");
        Assert.True(rightAtRight > rightAtLeft * 2, $"R: {rightTone}={rightAtRight}, {leftTone}={rightAtLeft}");
        Assert.True(mux.Stereo, "19 kHz pilot should lock");
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
        // Wide FM must stay ahead of the 2.4 MS/s dongle; Debug builds are slower than Release.
        Assert.True(
            clock.Elapsed.TotalSeconds < seconds * 2.0,
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

    [Fact]
    public void Packet_mode_demodulates_fm_audio_at_output_rate()
    {
        const double seconds = 0.2;
        Complex[] iq = Tone(seconds, offsetHz: 0, envelope: null);
        DemodResult result = Demodulator.Demodulate(iq, RadioMode.Packet, normalize: false);

        int expected = (int)(seconds * Demodulator.OutputRate);
        Assert.InRange(result.Audio.Length, (int)(expected * 0.8), (int)(expected * 1.05));
        Assert.Equal(Demodulator.OutputRate, result.SampleRate);
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

    private static float[] Mono(float[] audio, int channels)
    {
        if (channels <= 1)
            return audio;
        int frames = audio.Length / channels;
        var mono = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++)
                sum += audio[i * channels + c];
            mono[i] = sum / channels;
        }

        return mono;
    }

    private static float[] Channel(float[] interleaved, int channel, int channels)
    {
        int frames = interleaved.Length / channels;
        var samples = new float[frames];
        for (int i = 0; i < frames; i++)
            samples[i] = interleaved[i * channels + channel];
        return samples;
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
