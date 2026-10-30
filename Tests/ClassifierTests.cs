using System.Numerics;
using AgenticSdr.Dsp;

namespace AgenticSdr.Tests;

/// <summary>Checks that peak width and shape become CW, AM, sideband, narrow FM, or FT8 the way the band plan expects.</summary>
public class ClassifierTests
{
    private const double SampleRate = 2_400_000;
    private const int Fft = 4096;

    [Fact]
    public void Narrow_spike_is_cw()
    {
        SpectralPeak peak = Strongest(Tone(10_000, -40));
        Classification result = ModeClassifier.Classify(peak, hint: null);
        Assert.Equal(RadioMode.Cw, result.Mode);
        Assert.True(result.Confidence >= 0.7);
    }

    [Fact]
    public void Wide_flat_signal_is_nbfm_even_with_a_cw_hint()
    {
        SpectralPeak peak = Strongest(Plateau(25_000, 6_000, -42));
        var hint = new BandHint(RadioMode.Cw, "2 m CW");
        Classification result = ModeClassifier.Classify(peak, hint);
        Assert.Equal(RadioMode.Nbfm, result.Mode);
    }

    [Fact]
    public void Energy_above_the_carrier_is_usb()
    {
        double[] db = Floor();
        int carrier = Bin(8_000);
        db[carrier] = -42;
        for (int i = carrier + 1; i <= carrier + 5; i++)
            db[i] = -50;
        SpectralPeak peak = Strongest(db);
        Assert.Equal(RadioMode.Usb, ModeClassifier.Classify(peak, hint: null).Mode);
    }

    [Fact]
    public void Energy_below_the_carrier_is_lsb()
    {
        double[] db = Floor();
        int carrier = Bin(8_000);
        db[carrier] = -42;
        for (int i = carrier - 5; i < carrier; i++)
            db[i] = -50;
        SpectralPeak peak = Strongest(db);
        Assert.Equal(RadioMode.Lsb, ModeClassifier.Classify(peak, hint: null).Mode);
    }

    [Fact]
    public void Carrier_with_symmetric_sidebands_is_am()
    {
        double[] db = Floor();
        int carrier = Bin(40_000);
        db[carrier] = -30;
        for (int i = 1; i <= 8; i++)
        {
            db[carrier - i] = -44;
            db[carrier + i] = -44;
        }

        SpectralPeak peak = Strongest(db);
        Assert.Equal(RadioMode.Am, ModeClassifier.Classify(peak, hint: null).Mode);
    }

    [Fact]
    public void Asymmetric_station_is_reported_at_the_loudest_bin()
    {
        double[] db = Floor();
        int loud = Bin(20_000);
        for (int i = -40; i <= 10; i++)
            db[loud + i] = -40;
        db[loud] = -30;
        var peaks = PeakPicker.Find(db, centerHz: 144_000_000, SampleRate, thresholdDb: 8, maxBandwidthHz: 80_000);
        Assert.NotEmpty(peaks);
        Assert.InRange(peaks[0].FrequencyHz, 144_018_000, 144_022_000);
    }

    [Fact]
    public void Hf_voice_without_a_hint_defaults_to_lsb_below_10_mhz()
    {
        Assert.Equal(RadioMode.Lsb, ModeClassifier.SideFromFrequency(7_200_000));
        Assert.Equal(RadioMode.Usb, ModeClassifier.SideFromFrequency(14_200_000));
    }

    private static SpectralPeak Strongest(double[] db)
    {
        var peaks = PeakPicker.Find(db, centerHz: 144_000_000, SampleRate, thresholdDb: 8);
        Assert.NotEmpty(peaks);
        return peaks[0];
    }

    private static double[] Tone(double offsetHz, double powerDb)
    {
        double[] db = Floor();
        db[Bin(offsetHz)] = powerDb;
        return db;
    }

    private static double[] Plateau(double offsetHz, double halfWidthHz, double powerDb)
    {
        double[] db = Floor();
        int center = Bin(offsetHz);
        int bins = Math.Max(1, (int)Math.Round(halfWidthHz / (SampleRate / Fft)));
        for (int i = center - bins; i <= center + bins; i++)
            db[i] = powerDb;
        return db;
    }

    private static double[] Floor()
    {
        var db = new double[Fft];
        Array.Fill(db, -100);
        return db;
    }

    private static int Bin(double offsetHz) =>
        (int)Math.Round(offsetHz / (SampleRate / Fft) + Fft / 2.0);
}

/// <summary>Checks that promoting one direct-sampling channel keeps the real tone stronger than its mirror.</summary>
public class AnalyticTests
{
    [Fact]
    public void Cosine_becomes_a_positive_frequency()
    {
        const int n = 4096;
        const double rate = 48_000;
        var real = new float[n];
        for (int i = 0; i < n; i++)
            real[i] = (float)Math.Cos(2 * Math.PI * 4_800 * i / rate);

        Complex[] iq = AnalyticSignal.Promote(real);
        double[] db = SpectrumAnalyzer.AveragePowerDb(iq, n);
        double negative = db.Take(n / 2 - 20).Max();
        double positive = db.Skip(n / 2 + 20).Max();
        Assert.True(positive > negative + 8, $"positive {positive} dB, negative {negative} dB");
    }
}
