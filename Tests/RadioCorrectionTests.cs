using AgenticSdr.Radio;
using AgenticSdr.Shared;

namespace AgenticSdr.Tests;

public class RadioCorrectionTests
{
    [Fact]
    public void Identity_prefers_serial_then_open_args()
    {
        var withSerial = new RadioDeviceInfo(0, "NESDR", "Nooelec", "SMArt", "00000001", "rtlsdr", 0);
        Assert.Equal("rtlsdr:serial:00000001", RadioIdentity.Key(withSerial));

        var withArgs = new RadioDeviceInfo(1, "Airspy", null, null, null, "airspy", 0, "driver=airspy");
        Assert.Equal("airspy:args:driver=airspy", RadioIdentity.Key(withArgs));

        var byIndex = new RadioDeviceInfo(2, "Unknown", null, null, null, "hackrf", 3);
        Assert.Equal("hackrf:index:3", RadioIdentity.Key(byIndex));
    }

    [Fact]
    public void Store_persists_per_radio_and_falls_back()
    {
        string path = Path.Combine(Path.GetTempPath(), "agentic-sdr-ppm-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new RadioCorrectionStore(path);
            var a = new RadioDeviceInfo(0, "NESDR A", null, null, "AAA", "rtlsdr", 0);
            var b = new RadioDeviceInfo(1, "NESDR B", null, null, "BBB", "rtlsdr", 1);

            Assert.Equal(5, store.Resolve(a, fallbackPpm: 5));
            Assert.Null(store.Get(a));

            store.Set(a, -12);
            Assert.Equal(-12, store.Resolve(a, fallbackPpm: 5));
            Assert.Equal(5, store.Resolve(b, fallbackPpm: 5));

            var again = new RadioCorrectionStore(path);
            Assert.Equal(-12, again.Resolve(a, fallbackPpm: 0));
            Assert.Equal(-12, again.Get(a));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Store_rejects_out_of_range_ppm()
    {
        string path = Path.Combine(Path.GetTempPath(), "agentic-sdr-ppm-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new RadioCorrectionStore(path);
            var info = new RadioDeviceInfo(0, "NESDR", null, null, "CCC", "rtlsdr", 0);
            Assert.Throws<ArgumentOutOfRangeException>(() => store.Set(info, 501));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Lime_lo_correction_moves_opposite_the_crystal_error()
    {
        Assert.Equal(100_000_000, LimeDevice.CorrectLo(100_000_000, 0));
        double lo = LimeDevice.CorrectLo(100_000_000, 10);
        Assert.True(lo < 100_000_000);
        Assert.InRange(lo, 99_998_900, 99_999_100);
    }
}
