using AgenticSdr.Dsp;

namespace AgenticSdr.Tests;

/// <summary>Regression: Olivia must still copy keyboard text several dB into the noise.</summary>
public class OliviaWeakProbe
{
    [Fact]
    public void Olivia_recovers_text_several_db_below_noise()
    {
        var profile = new OliviaProfile(8, 250, "8/250");
        float[] clean = OliviaDecoder.Modulate(
            "CQ DE K7ABC TEST", profile, OliviaDecoder.SampleRate, centerHz: OliviaDecoder.CenterHz);
        float[] noisy = AddNoise(clean, snrDb: -6, seed: 7);
        IReadOnlyList<OliviaMessage> messages = OliviaDecoder.Decode(noisy, OliviaDecoder.SampleRate);
        Assert.NotEmpty(messages);
        Assert.Contains("CQ DE K7ABC", messages[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Classifier_keeps_olivia_hint_on_weak_narrow_peak()
    {
        var peak = new SpectralPeak(14_073_000, -8, -50, 10, 400, 14_072_800, 14_073_200, 0, 0.1, false);
        Classification hit = ModeClassifier.Classify(peak, new BandHint(RadioMode.Olivia, CallingName: "20 m Olivia"));
        Assert.Equal(RadioMode.Olivia, hit.Mode);
    }

    private static float[] AddNoise(float[] clean, double snrDb, int seed)
    {
        double signalPower = 0;
        for (int i = 0; i < clean.Length; i++)
            signalPower += clean[i] * clean[i];
        signalPower /= clean.Length;
        double noisePower = signalPower / Math.Pow(10, snrDb / 10.0);
        double sigma = Math.Sqrt(Math.Max(noisePower, 1e-18));
        var rng = new Random(seed);
        var outWave = new float[clean.Length];
        for (int i = 0; i < clean.Length; i++)
            outWave[i] = clean[i] + (float)(sigma * NextGaussian(rng));
        return outWave;
    }

    private static double NextGaussian(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = 1.0 - rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }
}
