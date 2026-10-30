using AgenticSdr.Dsp;

namespace AgenticSdr.Tests;

/// <summary>Checks that voice audio keeps a speech tone, rejects hiss, and pulls out a single-sample pop.</summary>
public class VoiceCleanerTests
{
    [Fact]
    public void A_speech_tone_is_kept_and_hiss_is_not()
    {
        const int rate = 48_000;
        float[] voice = Tone(rate, 1_000, 0.4);
        float[] hiss = Tone(rate, 8_000, 0.4);
        var cleaner = new VoiceCleaner(rate, 300, 3_400);
        cleaner.Process(voice);
        new VoiceCleaner(rate, 300, 3_400).Process(hiss);
        Assert.True(Rms(voice) > 0.05, $"voice RMS {Rms(voice)}");
        Assert.True(Rms(hiss) < Rms(voice) * 0.2, $"hiss {Rms(hiss)} voice {Rms(voice)}");
    }

    [Fact]
    public void Analog_speech_is_cleaned_and_cw_and_digital_are_not()
    {
        Assert.True(VoiceCleaner.IsVoice(RadioMode.Nbfm));
        Assert.True(VoiceCleaner.IsVoice(RadioMode.Wbfm));
        Assert.True(VoiceCleaner.IsVoice(RadioMode.Am));
        Assert.True(VoiceCleaner.IsVoice(RadioMode.Usb));
        Assert.True(VoiceCleaner.IsVoice(RadioMode.Lsb));
        Assert.False(VoiceCleaner.IsVoice(RadioMode.Cw));
        Assert.False(VoiceCleaner.IsVoice(RadioMode.Ft8));
        Assert.False(VoiceCleaner.IsVoice(RadioMode.Dmr));
        Assert.False(VoiceCleaner.IsVoice(RadioMode.Unknown));

        const double seconds = 0.25;
        const double tone = 6_000;
        int count = (int)(seconds * Demodulator.InputRate);
        var iq = new System.Numerics.Complex[count];
        double phase = 0;
        for (int i = 0; i < count; i++)
        {
            double env = 1 + 0.6 * Math.Sin(2 * Math.PI * tone * i / Demodulator.InputRate);
            iq[i] = new System.Numerics.Complex(env * Math.Cos(phase), env * Math.Sin(phase));
        }

        float[] cleaned = Finish(iq, cleanVoice: true);
        float[] digital = Finish(iq, cleanVoice: false);
        double cleanedTone = Correlate(cleaned, Demodulator.OutputRate, tone);
        double keptTone = Correlate(digital, Demodulator.OutputRate, tone);
        Assert.True(keptTone > cleanedTone * 4, $"unfiltered {keptTone}, filtered {cleanedTone}");
    }

    [Fact]
    public void A_single_sample_pop_is_pulled_down()
    {
        const int rate = 48_000;
        float[] audio = Tone(rate, 500, 0.1);
        int at = rate / 4;
        audio[at] = 1f;
        new VoiceCleaner(rate, 300, 3_400).Process(audio);
        float worst = 0;
        for (int i = at - 2; i <= at + 4 && i < audio.Length; i++)
            worst = Math.Max(worst, Math.Abs(audio[i]));
        Assert.True(worst < 0.35, $"pop left at {worst}");
    }

    private static float[] Tone(int rate, double frequency, double amplitude)
    {
        var audio = new float[rate / 2];
        for (int i = 0; i < audio.Length; i++)
            audio[i] = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * i / rate));
        return audio;
    }

    private static double Rms(float[] audio)
    {
        int start = audio.Length / 5;
        double sum = 0;
        int count = 0;
        for (int i = start; i < audio.Length; i++)
        {
            sum += audio[i] * audio[i];
            count++;
        }

        return Math.Sqrt(sum / count);
    }

    private static float[] Finish(System.Numerics.Complex[] iq, bool cleanVoice)
    {
        var demod = new Demodulator(RadioMode.Am, cleanVoice: cleanVoice);
        demod.Push(iq);
        return demod.Finish(normalize: false).Audio;
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
