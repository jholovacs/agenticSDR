namespace AgenticSdr.Radio;

/// <summary>
/// The list of USB radios and the way to open one of them.
/// RTL-SDR dongles come from librtlsdr. Anything else comes from SoapySDR when that library is installed.
/// A dongle already listed by librtlsdr is not listed a second time.
/// </summary>
public static class RadioDevices
{
    public static IReadOnlyList<RadioDeviceInfo> List()
    {
        var devices = new List<RadioDeviceInfo>();
        var rtlSerials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (RadioDeviceInfo info in RtlSdrDevice.List())
            {
                devices.Add(info with { Index = devices.Count, Driver = "rtlsdr", DriverIndex = info.Index });
                if (!string.IsNullOrWhiteSpace(info.Serial))
                    rtlSerials.Add(info.Serial);
            }
        }
        catch (RadioException)
        {
            // librtlsdr is optional once SoapySDR is installed.
        }

        foreach (RadioDeviceInfo info in SoapyDevice.TryList())
        {
            if (IsDuplicateRtl(info, rtlSerials))
                continue;
            devices.Add(info with { Index = devices.Count });
        }

        return devices;
    }

    public static IRadioDevice Open(RadioDeviceInfo info, double sampleRateHz, double? gainDb, int ppm)
    {
        if (info.Driver.Equals("rtlsdr", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(info.OpenArgs))
            return RtlSdrDevice.Open(info.DriverIndex, sampleRateHz, gainDb, ppm);

        if (string.IsNullOrWhiteSpace(info.OpenArgs))
            throw new RadioException($"No open arguments for {info.Name}.");

        return SoapyDevice.Open(info.OpenArgs, sampleRateHz, gainDb, ppm);
    }

    private static bool IsDuplicateRtl(RadioDeviceInfo info, HashSet<string> rtlSerials)
    {
        if (!info.Driver.Equals("rtlsdr", StringComparison.OrdinalIgnoreCase))
            return false;
        if (rtlSerials.Count == 0)
            return false;
        if (string.IsNullOrWhiteSpace(info.Serial))
            return true;
        return rtlSerials.Contains(info.Serial);
    }
}
