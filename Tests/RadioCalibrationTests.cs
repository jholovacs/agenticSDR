using AgenticSdr.Dsp;
using AgenticSdr.Host;

namespace AgenticSdr.Tests;

public class RadioCalibrationTests
{
    [Fact]
    public void Ppm_from_measured_peak_matches_fast_crystal_sign()
    {
        // Peak appears 5.371 kHz low on 144.390 → ~+37 ppm correction.
        double ppm = RadioCalibration.PpmFromMeasured(144_390_000, 144_384_629);
        Assert.InRange(ppm, 37.0, 37.5);
    }

    [Fact]
    public void Combine_uses_median_and_rounds()
    {
        var obs = new[]
        {
            new PpmObservation("a", 162_400_000, 162_400_000 - 1_624, 20, 10.0),
            new PpmObservation("b", 162_550_000, 162_550_000 - 6_502, 18, 40.0),
            new PpmObservation("c", 162_475_000, 162_475_000 - 4_062, 15, 25.0)
        };
        Assert.Equal(25, RadioCalibration.CombinePpm(obs));
    }

    [Fact]
    public void Calibration_references_are_noaa_nbfm_carriers()
    {
        IReadOnlyList<CallingChannel> refs = Region2BandPlan.CalibrationReferences();
        Assert.NotEmpty(refs);
        Assert.All(refs, c =>
        {
            Assert.Equal(RadioMode.Nbfm, c.Mode);
            Assert.InRange(c.FrequencyHz, 162_400_000, 162_550_000);
        });
    }

    [Fact]
    public void Search_window_covers_uncalibrated_vhf_sticks()
    {
        Assert.True(RadioCalibration.SearchHz(162_550_000) >= 40_000);
        Assert.InRange(RadioCalibration.SearchHz(162_550_000), 16_000, 50_000);
    }
}
