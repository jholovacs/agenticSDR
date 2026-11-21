using System.Numerics;
using AgenticSdr.Dsp;
using AgenticSdr.Processors;

namespace AgenticSdr.Tests;

public class ModeAnalyzeTests
{
    [Fact]
    public void Envelope_margin_expands_passband_by_ten_percent()
    {
        Assert.True(ModeBandwidth.FitsWithMargin(RadioMode.Cw, 75, 0.10)); // below Min 80, within 10%
        Assert.False(ModeBandwidth.FitsWithMargin(RadioMode.Cw, 50, 0.10));
        Assert.True(ModeBandwidth.FitsWithMargin(RadioMode.Nbfm, 17_500, 0.10)); // above Max 16k
        Assert.True(ModeBandwidth.FitsWithMargin(RadioMode.Wspr, 5, 0.10));
    }

    [Fact]
    public void Candidates_include_forced_prior_even_when_envelope_mismatches()
    {
        IReadOnlyList<IModeAnalyzer> list = ModeAnalyzerCatalog.Candidates(
            bandwidthHz: 12_000,
            priorMode: RadioMode.Wspr,
            hintMode: null);
        Assert.Contains(list, a => a.Mode == RadioMode.Wspr);
        Assert.Contains(list, a => a.Mode == RadioMode.Nbfm);
    }

    [Fact]
    public void Dmr_framing_outranks_plain_nbfm()
    {
        Complex[] iq = DmrBurst();
        SpectralPeak peak = Peak(146_520_000, bandwidthHz: 12_000, snrDb: 12, asymmetry: 0, carrier: 2);
        var ctx = new ModeAnalysisContext(146_520_000, peak, iq, 146_520_000, 48_000);
        ModeAnalysis dmr = new DmrAnalyzer().Analyze(in ctx);
        ModeAnalysis nbfm = new NbfmAnalyzer().Analyze(in ctx);
        Assert.True(dmr.DecodeConfirmed);
        Assert.True(dmr.Confidence > nbfm.Confidence);
    }

    [Fact]
    public void Dstar_header_outranks_plain_nbfm()
    {
        Complex[] iq = DstarSync.ModulateHeaderIq(0x40, "REF001BL", "W1AW  C", "CQCQCQ", "W1AW", "RPT ");
        SpectralPeak peak = Peak(145_670_000, bandwidthHz: 10_000, snrDb: 10, asymmetry: 0, carrier: 1);
        var ctx = new ModeAnalysisContext(145_670_000, peak, iq, 145_670_000, 48_000);
        ModeAnalysis dstar = new DstarAnalyzer().Analyze(in ctx);
        ModeAnalysis nbfm = new NbfmAnalyzer().Analyze(in ctx);
        Assert.True(dstar.DecodeConfirmed);
        Assert.True(dstar.Confidence > nbfm.Confidence);
    }

    [Fact]
    public void Am_like_peak_prefers_am_over_ssb()
    {
        Complex[] iq = ToneIq(48_000, 0.05);
        SpectralPeak peak = Peak(1_000_000, bandwidthHz: 8_000, snrDb: 20, asymmetry: 0.05, carrier: 14);
        var ctx = new ModeAnalysisContext(1_000_000, peak, iq, 1_000_000, 48_000);
        ModeAnalysis am = new AmAnalyzer().Analyze(in ctx);
        ModeAnalysis usb = new UsbAnalyzer().Analyze(in ctx);
        Assert.True(am.Confidence > usb.Confidence);
    }

    [Fact]
    public void Narrow_strong_peak_scores_cw()
    {
        Complex[] iq = ToneIq(48_000, 0.05);
        SpectralPeak peak = Peak(7_050_000, bandwidthHz: 200, snrDb: 18, asymmetry: 0, carrier: 0);
        var ctx = new ModeAnalysisContext(7_050_000, peak, iq, 7_050_000, 48_000, BandHintMode: RadioMode.Cw);
        ModeAnalysis cw = new CwAnalyzer().Analyze(in ctx);
        Assert.True(cw.Confidence >= 0.45);
    }

    private static SpectralPeak Peak(double hz, double bandwidthHz, double snrDb, double asymmetry, double carrier) =>
        new(hz, -40, -60, snrDb, bandwidthHz, hz - bandwidthHz / 2, hz + bandwidthHz / 2, carrier, asymmetry, false);

    private static Complex[] ToneIq(int rate, double seconds)
    {
        int n = (int)(rate * seconds);
        var iq = new Complex[n];
        double phase = 0;
        double step = 2 * Math.PI * 1_000 / rate;
        for (int i = 0; i < n; i++)
        {
            phase += step;
            iq[i] = new Complex(Math.Cos(phase), Math.Sin(phase));
        }

        return iq;
    }

    private static Complex[] DmrBurst()
    {
        // Repeat a published DMR sync word so Present() has enough symbols.
        int[] levels = Levels("DFF57D75DF5D");
        const int rate = 48_000;
        const int reps = 8;
        var iq = new Complex[levels.Length * 10 * reps];
        double phase = 0;
        int at = 0;
        for (int rep = 0; rep < reps; rep++)
        {
            foreach (int level in levels)
            {
                double step = 2 * Math.PI * (level * 648.0) / rate;
                for (int s = 0; s < 10; s++)
                {
                    phase += step;
                    iq[at++] = new Complex(Math.Cos(phase), Math.Sin(phase));
                }
            }
        }

        return iq;
    }

    private static int[] HexBits(string hex)
    {
        var bits = new List<int>(hex.Length * 4);
        foreach (char c in hex)
        {
            int value = Convert.ToInt32(c.ToString(), 16);
            for (int shift = 3; shift >= 0; shift--)
                bits.Add((value >> shift) & 1);
        }

        return bits.ToArray();
    }

    private static int[] Levels(string hex)
    {
        int[] bits = HexBits(hex);
        var levels = new int[bits.Length / 2];
        for (int i = 0; i < levels.Length; i++)
        {
            int dibit = (bits[i * 2] << 1) | bits[i * 2 + 1];
            levels[i] = dibit switch
            {
                0b01 => 3,
                0b00 => 1,
                0b10 => -1,
                _ => -3
            };
        }

        return levels;
    }
}
