using AgenticSdr.Dsp;
using AgenticSdr.Radio;

namespace AgenticSdr.Tests;

/// <summary>Opens the NESDR when it is plugged in and checks that the spectrum is not a flat line. Skips cleanly when no dongle is present.</summary>
public class DeviceTests
{
    [Fact]
    public void Live_radio_is_not_a_flat_spectrum()
    {
        IReadOnlyList<RadioDeviceInfo> devices;
        try
        {
            devices = RtlSdrDevice.List();
        }
        catch (RadioException ex)
        {
            throw new InvalidOperationException("librtlsdr should load from native/rtlsdr.dll (or the system librtlsdr package).", ex);
        }

        if (devices.Count == 0)
        {
            Console.WriteLine("SKIP: no RTL-SDR is plugged in.");
            return;
        }

        using RtlSdrDevice radio = RtlSdrDevice.Open(0, RadioLimits.DefaultSampleRateHz, gainDb: 28, ppm: 0);
        radio.Tune(162_550_000);
        var buffer = new byte[262_144];
        radio.Read(buffer);
        int read = radio.Read(buffer);
        Assert.True(read > 10_000);
        (var iq, _) = Iq.Convert(buffer.AsSpan(0, read / 2 * 2), directSampling: false);
        double[] db = SpectrumAnalyzer.AveragePowerDb(iq, 4096);
        double range = db.Max() - db.Min();
        Assert.True(range > 3, $"Spectrum dynamic range was only {range:0.0} dB.");
    }
}
