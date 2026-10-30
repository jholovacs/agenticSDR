using AgenticSdr.Dsp;
using AgenticSdr.Host;
using AgenticSdr.Radio;

namespace AgenticSdr.Tests;

/// <summary>Checks calling-frequency labels, FT8 dials, and the names for FM broadcast, AM broadcast, and NOAA.</summary>
public class BandPlanTests
{
    [Fact]
    public void Calling_frequencies_and_segments_label_known_spots()
    {
        Assert.Equal("2 m national FM simplex calling", Region2BandPlan.SourceLabel(146_520_000));
        Assert.Equal(RadioMode.Nbfm, Region2BandPlan.HintAt(146_520_000).PreferredMode);
        Assert.Equal("2 m CW", Region2BandPlan.HintAt(144_050_000).SegmentName);
        Assert.Equal(RadioMode.Cw, Region2BandPlan.HintAt(144_050_000).PreferredMode);
        Assert.Equal("2 m FM repeater output", Region2BandPlan.SourceLabel(147_000_000));
        Assert.Equal("40 m LSB", Region2BandPlan.SourceLabel(7_200_000));
        Assert.Equal(RadioMode.Lsb, Region2BandPlan.HintAt(7_200_000).PreferredMode);
        Assert.Equal("20 m FT8", Region2BandPlan.SourceLabel(14_074_000));
        Assert.Equal(RadioMode.Ft8, Region2BandPlan.HintAt(14_075_200).PreferredMode);
        Assert.True(Region2BandPlan.TryFt8Dial(14_075_500, out double dial));
        Assert.Equal(14_074_000, dial);
        Assert.False(Region2BandPlan.TryFt8Dial(14_200_000, out _));
        Assert.Equal("20 m USB", Region2BandPlan.SourceLabel(14_200_000));
        Assert.Equal(RadioMode.Usb, Region2BandPlan.HintAt(14_200_000).PreferredMode);
        Assert.Equal("6 m SSB calling", Region2BandPlan.SourceLabel(50_125_000));
        Assert.Equal("2 m FM", Region2BandPlan.SourceLabel(146_080_000));
        Assert.Equal("Outside the region 2 amateur plan", Region2BandPlan.SourceLabel(70_000_000));
        Assert.Equal("FM broadcast", Region2BandPlan.SourceLabel(98_500_000));
        Assert.Equal("AM broadcast", Region2BandPlan.SourceLabel(1_000_000));
        Assert.Equal("NOAA WX1", Region2BandPlan.SourceLabel(162_550_000));
        Assert.Equal("CB 19 highway", Region2BandPlan.SourceLabel(27_185_000));
        Assert.Equal("CB 9 emergency", Region2BandPlan.SourceLabel(27_065_000));
        Assert.Equal("GMRS 20", Region2BandPlan.SourceLabel(462_675_000));
        Assert.Equal("GMRS 15 input", Region2BandPlan.SourceLabel(467_550_000));
        Assert.Equal(RadioMode.Am, Region2BandPlan.HintAt(27_185_000).PreferredMode);
        Assert.Equal(RadioMode.Nbfm, Region2BandPlan.HintAt(462_550_000).PreferredMode);
        Assert.False(Region2BandPlan.Covers(Region2BandPlan.Find("gmrs")!, 465_000_000));
        Assert.True(Region2BandPlan.Covers(Region2BandPlan.Find("dmr")!, 446_000_000));
        Assert.Equal(RadioMode.Wbfm, Region2BandPlan.HintAt(98_500_000).PreferredMode);
    }

    [Fact]
    public void Two_meter_hops_cover_the_band()
    {
        var centers = HopPlanner.Centers(144_000_000, 148_000_000, RadioLimits.DefaultSampleRateHz, RadioLimits.HopStepHz);
        Assert.NotEmpty(centers);
        for (double frequency = 144_000_000; frequency <= 148_000_000; frequency += 50_000)
            Assert.Contains(centers, center => Math.Abs(center - frequency) <= RadioLimits.DefaultSampleRateHz / 2);
    }

    [Fact]
    public void A_narrow_band_is_tuned_beside_its_middle()
    {
        var centers = HopPlanner.Centers(162_400_000, 162_550_000, RadioLimits.DefaultSampleRateHz, RadioLimits.HopStepHz);
        double center = Assert.Single(centers);
        Assert.True(center < 162_400_000 || center > 162_550_000);
        Assert.InRange(162_400_000, center - RadioLimits.DefaultSampleRateHz / 2, center + RadioLimits.DefaultSampleRateHz / 2);
        Assert.InRange(162_550_000, center - RadioLimits.DefaultSampleRateHz / 2, center + RadioLimits.DefaultSampleRateHz / 2);
    }
}

/// <summary>Checks Maidenhead grid parsing and the repeater-list matcher.</summary>
public class LocationTests
{
    [Fact]
    public void Grid_jj00_is_just_north_and_east_of_the_origin()
    {
        Assert.True(Maidenhead.TryParse("JJ00", out double latitude, out double longitude));
        Assert.Equal(0.5, latitude, 3);
        Assert.Equal(1, longitude, 3);
    }

    [Fact]
    public void Repeater_match_picks_the_nearest_callsign()
    {
        const string json = """
            {"results":[
              {"Callsign":"W1ABC","Frequency":"146.520"},
              {"Callsign":"K2XYZ","Frequency":"147.000"}
            ]}
            """;
        Assert.Equal("W1ABC", RepeaterLookup.MatchCallsign(json, 146_520_000));
        Assert.Equal("K2XYZ", RepeaterLookup.MatchCallsign(json, 147_005_000));
        Assert.Null(RepeaterLookup.MatchCallsign(json, 150_000_000));
    }
}

/// <summary>Checks that a WAV file starts with the header a browser expects.</summary>
public class WavTests
{
    [Fact]
    public void Wav_header_describes_mono_pcm()
    {
        byte[] wav = WavFile.Write([0.5f, -0.5f, 0f], 48_000);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal(48_000, BitConverter.ToInt32(wav, 24));
        Assert.Equal(1, BitConverter.ToInt16(wav, 22));
        Assert.Equal(44 + 6, wav.Length);
    }
}

/// <summary>Checks that RTL-SDR direct sampling is used below 24 MHz and the tuner is used at and above it.</summary>
public class DirectSamplingPolicyTests
{
    [Theory]
    [InlineData(14_200_000, 2)]
    [InlineData(23_999_999, 2)]
    [InlineData(24_000_000, 0)]
    [InlineData(146_520_000, 0)]
    public void Hf_uses_q_branch_direct_sampling(double frequencyHz, int mode)
    {
        Assert.Equal(mode, RadioLimits.DirectSamplingModeFor(frequencyHz));
    }

    [Fact]
    public void Out_of_range_frequencies_are_rejected()
    {
        Assert.Throws<RadioException>(() => RadioLimits.DirectSamplingModeFor(50_000));
        Assert.Throws<RadioException>(() => RadioLimits.DirectSamplingModeFor(2_000_000_000));
    }
}
