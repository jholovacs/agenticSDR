using AgenticSdr.Dsp;
using AgenticSdr.Host;

namespace AgenticSdr.Tests;

/// <summary>A scan has to notice speech that comes and goes, and it has to use the width of that mode.</summary>
public class SpeechActivityTests
{
    [Fact]
    public void A_band_is_watched_for_at_least_fifteen_seconds()
    {
        Assert.Equal(15_000, HopPlanner.DwellMilliseconds(1));
        Assert.Equal(7_500, HopPlanner.DwellMilliseconds(2));
        Assert.Equal(3_000, HopPlanner.DwellMilliseconds(10));
        Assert.True(HopPlanner.DwellMilliseconds(10) * 10 >= HopPlanner.MinBandMilliseconds);
    }

    [Fact]
    public void Sideband_voice_uses_a_narrower_window_than_narrow_fm()
    {
        ModeBandwidth.Pass ssb = ModeBandwidth.For(RadioMode.Usb)!.Value;
        ModeBandwidth.Pass nbfm = ModeBandwidth.For(RadioMode.Nbfm)!.Value;
        ModeBandwidth.Pass wbfm = ModeBandwidth.For(RadioMode.Wbfm)!.Value;
        Assert.InRange(ssb.WidthHz, 2_400, 3_000);
        Assert.InRange(nbfm.WidthHz, 10_000, 16_000);
        Assert.InRange(wbfm.WidthHz, 150_000, 250_000);
        Assert.True(ssb.MaxHz < nbfm.MaxHz);
    }

    [Fact]
    public void Two_voice_bursts_are_kept_and_a_single_spike_is_not()
    {
        double[][] frames = Quiet();
        foreach (int frame in new[] { 2, 3, 4, 12, 13, 14 })
            Raise(frames, frame, 4, 12);
        List<SpeechActivity.Hit> pulsed = Search(frames, RadioMode.Usb);
        Assert.Contains(pulsed, hit => hit.Intermittent);

        frames = Quiet();
        Raise(frames, 5, 4, 12);
        List<SpeechActivity.Hit> spike = Search(frames, RadioMode.Usb);
        Assert.Empty(spike);
    }

    [Fact]
    public void A_phone_channel_is_also_watched_for_morse()
    {
        Assert.Contains(RadioMode.Cw, ModeBandwidth.ScanModes(RadioMode.Usb));
        Assert.Contains(RadioMode.Cw, ModeBandwidth.ScanModes(RadioMode.Lsb));
        Assert.Contains(RadioMode.Cw, ModeBandwidth.ScanModes(RadioMode.Am));
        Assert.Contains(RadioMode.Cw, ModeBandwidth.ScanModes(RadioMode.Nbfm));
        Assert.Equal(new[] { RadioMode.Cw }, ModeBandwidth.ScanModes(RadioMode.Cw).ToArray());
        Assert.DoesNotContain(RadioMode.Cw, ModeBandwidth.ScanModes(RadioMode.Wbfm));

        double[][] frames = Quiet();
        foreach (int frame in new[] { 2, 3, 4, 12, 13, 14 })
        {
            Raise(frames, frame, 4, 12);
            RaiseAt(frames, frame, 13_000, 1, 14);
        }

        List<SpeechActivity.Hit> voice = Search(frames, RadioMode.Usb);
        List<SpeechActivity.Hit> morse = Search(frames, RadioMode.Cw);
        Assert.Contains(voice, hit => hit.Mode == RadioMode.Usb);
        Assert.DoesNotContain(voice, hit => hit.Mode == RadioMode.Cw);
        Assert.Contains(morse, hit => hit.Mode == RadioMode.Cw);
        Assert.DoesNotContain(morse, hit => hit.Mode == RadioMode.Usb);

        List<SpeechActivity.Hit> both = SpeechActivity.Distinct(voice.Concat(morse));
        Assert.Contains(both, hit => hit.Mode == RadioMode.Usb);
        Assert.Contains(both, hit => hit.Mode == RadioMode.Cw);
    }

    [Fact]
    public void A_hump_wider_than_sideband_is_not_called_sideband()
    {
        double[][] frames = Quiet();
        foreach (int frame in new[] { 2, 3, 4, 12, 13, 14 })
            Raise(frames, frame, 24, 12);
        List<SpeechActivity.Hit> hits = Search(frames, RadioMode.Usb);
        Assert.Empty(hits);
    }

    private const int Bins = 128;
    private const double BinHz = 500;
    private const double Tune = 14_150_000;

    private static double[][] Quiet()
    {
        var frames = new double[20][];
        for (int i = 0; i < frames.Length; i++)
            frames[i] = new double[Bins];
        return frames;
    }

    private static void Raise(double[][] frames, int frame, int widthBins, double db) =>
        RaiseAt(frames, frame, 10_000, widthBins, db);

    private static void RaiseAt(double[][] frames, int frame, double offsetHz, int widthBins, double db)
    {
        int at = Bins / 2 + (int)Math.Round(offsetHz / BinHz);
        int half = widthBins / 2;
        for (int i = 0; i < widthBins; i++)
        {
            int bin = at - half + i;
            if (bin >= 0 && bin < Bins)
                frames[frame][bin] = db;
        }
    }

    private static List<SpeechActivity.Hit> Search(double[][] frames, RadioMode mode) =>
        SpeechActivity.Find(frames, Tune, Bins * BinHz, Tune - 20_000, Tune + 20_000, mode, 0.1);
}
