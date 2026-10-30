using System.Numerics;
using AgenticSdr.Dsp;
using AgenticSdr.Radio;

namespace AgenticSdr.Tests;

/// <summary>Checks sample-rate choice, hop spacing, the radio-family names, and that a missing SoapySDR library does not throw.</summary>
public class SdrSupportTests
{
    [Fact]
    public void Sample_rate_prefers_2_4_megasamples_inside_a_wide_range()
    {
        double rate = SdrRates.Choose([], [new FrequencySpan(2_000_000, 20_000_000)]);
        Assert.Equal(2_400_000, rate);
    }

    [Fact]
    public void Sample_rate_picks_768_kilohertz_from_airspy_hf_rates()
    {
        double rate = SdrRates.Choose([192_000, 384_000, 768_000], []);
        Assert.Equal(768_000, rate);
    }

    [Fact]
    public void Hop_step_stays_1_megahertz_at_the_rtl_rate()
    {
        Assert.Equal(RadioLimits.HopStepHz, RadioLimits.HopStep(2_400_000));
        Assert.Equal(768_000 * 0.4, RadioLimits.HopStep(768_000));
    }

    [Fact]
    public void Catalog_names_published_families()
    {
        Assert.Equal("HackRF", SdrCatalog.DisplayName("hackrf", null));
        Assert.Equal("HackRF One #0", SdrCatalog.DisplayName("hackrf", "HackRF One #0"));
        Assert.Contains("NESDR", SdrCatalog.Find("rtlsdr")!.Radios);
        Assert.Contains(SdrCatalog.Families, family => family.Driver == "airspyhf");
        Assert.Contains(SdrCatalog.Families, family => family.Driver == "sdrplay");
    }

    [Fact]
    public void Demod_at_768_kilohertz_lands_near_48_kilohertz()
    {
        (int first, int second, int audio) = DemodRates.Plan(768_000);
        Assert.Equal(4, first);
        Assert.Equal(4, second);
        Assert.Equal(48_000, audio);

        var iq = new Complex[76_800];
        DemodResult result = Demodulator.Demodulate(iq, RadioMode.Nbfm, inputRate: 768_000, normalize: false);
        Assert.InRange(result.SampleRate, 47_000, 49_000);
        Assert.InRange(result.Audio.Length, 3_000, 5_200);
    }

    [Fact]
    public void Soapy_list_does_not_throw_when_the_library_is_missing()
    {
        IReadOnlyList<RadioDeviceInfo> devices = SoapyDevice.TryList();
        Assert.NotNull(devices);
    }
}
