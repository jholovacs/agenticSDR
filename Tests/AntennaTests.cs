using AgenticSdr.Dsp;
using AgenticSdr.Host;

namespace AgenticSdr.Tests;

/// <summary>Checks that antenna length and direction pick FM broadcast, NOAA, 40 m, and local AM the way the guide describes.</summary>
public class AntennaTests
{
    [Fact]
    public void Quarter_wave_vertical_near_98_megahertz_selects_fm_broadcast()
    {
        double length = AntennaPlan.SpeedOfLight * 0.95 / (4 * 98_000_000);
        AntennaAdvice advice = AntennaPlan.Advise(AntennaPlan.Parse(length, "m", "vertical", null));
        Assert.Equal("fm-bc", advice.Bands[0].BandId);
        Assert.DoesNotContain(advice.Bands, band => band.BandId == "am-bc");
    }

    [Fact]
    public void Quarter_wave_vertical_near_noaa_includes_2_meters()
    {
        double length = AntennaPlan.SpeedOfLight * 0.95 / (4 * 162_550_000);
        AntennaAdvice advice = AntennaPlan.Advise(AntennaPlan.Parse(length, "m", "vertical", null));
        Assert.Equal("noaa", advice.Bands[0].BandId);
        Assert.Contains(advice.Bands, band => band.BandId == "2m");
    }

    [Fact]
    public void Half_wave_40_meter_dipole_aimed_north_names_broadside()
    {
        double meters = AntennaPlan.SpeedOfLight * 0.95 / (2 * 7_150_000);
        AntennaAdvice advice = AntennaPlan.Advise(AntennaPlan.Parse(meters / 0.3048, "ft", "horizontal", 0));
        Assert.Equal("40m", advice.Bands[0].BandId);
        Assert.Contains("90° E", advice.Summary);
        Assert.Contains("270° W", advice.Summary);
        Assert.Contains(advice.Bands, band => band.BandId == "am-bc");
        Assert.Contains(advice.Bands, band => band.BandId == "15m");
    }

    [Fact]
    public void Short_whip_does_not_recommend_am_broadcast()
    {
        AntennaAdvice advice = AntennaPlan.Advise(AntennaPlan.Parse(0.5, "m", "vertical", null));
        Assert.DoesNotContain(advice.Bands, band => band.BandId == "am-bc");
        Assert.DoesNotContain(advice.Bands, band => band.BandId == "fm-bc");
    }

    [Fact]
    public void A_loud_2_meter_floor_guesses_a_short_vertical()
    {
        SignalStrength[] readings =
        [
            new(7_150_000, -90),
            new(98_000_000, -85),
            new(146_000_000, -40),
            new(435_000_000, -70)
        ];
        Assert.True(AntennaGuess.TryFromFloors(readings, out AntennaSpec spec, out string note));
        Assert.Equal("vertical", spec.Orientation);
        Assert.InRange(spec.LengthMeters, 0.45, 0.55);
        Assert.Contains("horizontal", note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(AntennaPlan.Advise(spec).Bands, band => band.BandId == "2m");
    }

    [Fact]
    public void The_lowest_loud_spot_is_treated_as_the_natural_frequency()
    {
        SignalStrength[] readings =
        [
            new(7_150_000, -45),
            new(21_200_000, -50),
            new(98_000_000, -80),
            new(146_000_000, -82)
        ];
        Assert.True(AntennaGuess.TryFromFloors(readings, out AntennaSpec spec, out _));
        Assert.InRange(spec.LengthMeters, 9.5, 10.5);
        Assert.Contains(AntennaPlan.Advise(spec).Bands, band => band.BandId == "40m");
    }

    [Fact]
    public void A_uhf_hump_is_not_called_a_vhf_antenna()
    {
        SignalStrength[] readings =
        [
            new(7_150_000, -20),
            new(27_200_000, 26),
            new(98_000_000, 19),
            new(146_000_000, 20),
            new(300_000_000, 23),
            new(400_000_000, 22),
            new(420_000_000, 20),
            new(435_000_000, 17),
            new(850_000_000, -6)
        ];
        Assert.True(AntennaGuess.TryFromFloors(readings, out AntennaSpec spec, out _));
        Assert.Equal("vertical", spec.Orientation);
        Assert.InRange(spec.LengthMeters, 0.15, 0.20);
        Assert.Equal("70cm", AntennaPlan.Advise(spec).Bands[0].BandId);
    }

    [Fact]
    public void Tuner_edge_noise_does_not_hide_a_2_meter_antenna()
    {
        SignalStrength[] readings =
        [
            new(7_150_000, -90),
            new(27_200_000, 30),
            new(98_000_000, -85),
            new(146_000_000, -40),
            new(435_000_000, -70)
        ];
        Assert.True(AntennaGuess.TryFromFloors(readings, out AntennaSpec spec, out _));
        Assert.InRange(spec.LengthMeters, 0.45, 0.55);
        AntennaAdvice advice = AntennaPlan.Advise(spec);
        Assert.Contains(advice.Bands, band => band.BandId == "2m" && band.Harmonic == 1);
        Assert.DoesNotContain(advice.Bands, band => band.BandId == "70cm" && band.Harmonic == 1);
    }

    [Fact]
    public void A_broadcast_band_that_stands_alone_is_the_antenna()
    {
        SignalStrength[] readings =
        [
            new(7_150_000, -90),
            new(98_000_000, -40),
            new(146_000_000, -75),
            new(435_000_000, -80)
        ];
        Assert.True(AntennaGuess.TryFromFloors(readings, out AntennaSpec spec, out _));
        Assert.InRange(spec.LengthMeters, 0.70, 0.80);
        Assert.Equal("fm-bc", AntennaPlan.Advise(spec).Bands[0].BandId);
    }

    [Fact]
    public void An_even_floor_is_not_a_guess()
    {
        SignalStrength[] readings =
        [
            new(7_150_000, -70),
            new(98_000_000, -71),
            new(146_000_000, -69)
        ];
        Assert.False(AntennaGuess.TryFromFloors(readings, out _, out string note));
        Assert.Contains("even", note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Horizontal_antenna_requires_a_bearing()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() => AntennaPlan.Parse(20, "m", "horizontal", null));
        Assert.Contains("compass", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Broadcast_fm_wider_than_voice_classifies_as_wbfm()
    {
        var peak = new SpectralPeak(98_500_000, -10, -60, 50, 160_000, 98_420_000, 98_580_000, 4, 0.05, false);
        Classification result = ModeClassifier.Classify(peak, Region2BandPlan.HintAt(98_500_000));
        Assert.Equal(RadioMode.Wbfm, result.Mode);
    }
}
