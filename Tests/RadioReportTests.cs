using AgenticSdr.Radio;

namespace AgenticSdr.Tests;

public class RadioReportTests
{
    [Fact]
    public void Frequencies_use_the_usual_units()
    {
        Assert.Equal("146.52 MHz", RadioText.Hertz(146_520_000));
        Assert.Equal("100 kHz", RadioText.Hertz(100_000));
        Assert.Equal("3.072 MHz", RadioText.Hertz(3_072_000));
    }

    [Fact]
    public void A_radio_without_its_own_driver_still_lists_its_name()
    {
        var info = new RadioDeviceInfo(0, "Example SDR", "Maker", "Board", "ABC123", "example", 0, "driver=example");
        RadioReport report = RadioDevices.Describe(info, 2_400_000);

        Assert.Equal("Example SDR", report.Title);
        Assert.Contains(report.Facts, fact => fact.Label == "Manufacturer" && fact.Value == "Maker");
        Assert.Contains(report.Facts, fact => fact.Label == "Serial" && fact.Value == "ABC123");
        Assert.Contains(report.Facts, fact => fact.Label == "Open arguments" && fact.Value == "driver=example");
    }
}
