using AgenticSdr.Dsp;
using AgenticSdr.Host;
using AgenticSdr.Processors;
using AgenticSdr.Privilege;
using AgenticSdr.Wspr;

namespace AgenticSdr.Tests;

/// <summary>WSPR Type-1 pack/encode/decode and digimode plumbing.</summary>
public class WsprTests
{
    [Fact]
    public void Pack_matches_published_vectors()
    {
        Assert.Equal(259_435_617u, WsprDecoder.PackCall("M0SUP"));
        Assert.Equal(2_091_614u, WsprDecoder.PackGridPower("IO90", 30));
    }

    [Fact]
    public void Channel_symbols_are_four_level()
    {
        byte[] symbols = WsprDecoder.ChannelSymbols("M0SUP", "IO90", 30);
        Assert.Equal(162, symbols.Length);
        Assert.All(symbols, s => Assert.InRange(s, 0, 3));
    }

    [Fact]
    public void Round_trip_recovers_beacon_text()
    {
        float[] slot = WsprDecoder.ModulateSlot("M0SUP", "IO90", 30);
        IReadOnlyList<WsprMessage> messages = WsprDecoder.Decode(slot, WsprDecoder.SampleRate);
        Assert.NotEmpty(messages);
        Assert.Equal("M0SUP IO90 30", messages[0].Text);
    }

    [Fact]
    public void Mode_parse_and_pipeline_accept_wspr()
    {
        Assert.Equal(RadioMode.Wspr, ModeClassifier.Parse("wspr"));
        Assert.Equal("wspr", CampSchedule.Canonical(RadioMode.Wspr));
        Assert.Equal(["wspr"], ModePipelines.DefaultIds(RadioMode.Wspr));
    }

    [Fact]
    public void Band_plan_lists_wspr_dials()
    {
        Assert.True(Region2BandPlan.TryDigiDial(14_095_600, RadioMode.Wspr, out double dial));
        Assert.Equal(14_095_600, dial);
        Assert.Equal(RadioMode.Wspr, Region2BandPlan.HintAt(14_095_600).PreferredMode);
        Assert.Contains(Region2BandPlan.KnownServiceChannels(), g => g.Id == "wspr" && g.Channels.Count > 0);
    }

    [Fact]
    public void Transmitter_builds_baseband_without_radiation()
    {
        var op = new OperatorIdentity("M0SUP", "M0SUP", LicenseClass.Extra);
        Assert.True(OperatorClick.TryFromButton("wspr-1", true, out OperatorClick click));
        var result = WsprTransmitter.TryTransmit(
            op,
            click,
            new WsprTransmitRequest("M0SUP", "IO90", 30, 14_095_600, 5),
            radio: null);
        Assert.True(result.Ok);
        Assert.False(result.Radiated);
        Assert.NotNull(result.Samples);
        Assert.Equal(WsprDecoder.SampleRate * WsprDecoder.SlotSeconds, result.Samples!.Length);
    }
}
