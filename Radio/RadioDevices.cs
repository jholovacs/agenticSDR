namespace AgenticSdr.Radio;

/// <summary>
/// The list of USB radios and the way to open one of them.
/// RTL-SDR dongles come from librtlsdr. LimeSDR comes from LimeSuiteNG and stays receive-only.
/// Anything else comes from SoapySDR when that library is installed.
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
            // LimeSuiteNG opens these receive-only. The Soapy module turns the transmitter on.
            if (info.Driver.Equals("lime", StringComparison.OrdinalIgnoreCase))
                continue;
            devices.Add(info with { Index = devices.Count });
        }

        foreach (RadioDeviceInfo info in LimeDevice.TryList())
        {
            if (devices.Any(existing => SameRadio(existing, info)))
                continue;
            devices.Add(info with { Index = devices.Count });
        }

        return devices;
    }

    /// <summary>
    /// Facts for a radio that is not open. LimeSDR is connected only long enough to read its versions,
    /// and the receive channel is not enabled.
    /// </summary>
    public static RadioReport Describe(RadioDeviceInfo info, double preferredSampleRateHz)
    {
        if (info.Driver.Equals("lime", StringComparison.OrdinalIgnoreCase))
            return LimeDevice.Inspect(info, preferredSampleRateHz);
        if (info.Driver.Equals("rtlsdr", StringComparison.OrdinalIgnoreCase))
            return RtlSdrDevice.Inspect(info, preferredSampleRateHz, null);
        return ListedOnly(info);
    }

    /// <summary>Facts for the radio the session already has open. This does not open a second handle.</summary>
    public static RadioReport DescribeOpen(RadioDeviceInfo info, IRadioDevice device, double preferredSampleRateHz)
    {
        if (device is RtlSdrDevice rtl)
            return RtlSdrDevice.Inspect(info, preferredSampleRateHz, rtl);
        if (device is LimeDevice lime)
            return lime.Report(info, preferredSampleRateHz);
        return ListedOnly(info, device.Capabilities);
    }

    private static RadioReport ListedOnly(RadioDeviceInfo info, RadioCapabilities? capabilities = null)
    {
        var facts = new List<RadioFact>();
        RadioText.Identity(facts, info);
        RadioText.Add(facts, "Open arguments", info.OpenArgs);
        if (capabilities is not null)
            AddCapabilities(facts, capabilities);
        return new RadioReport(info.Name, facts);
    }

    internal static void AddCapabilities(List<RadioFact> facts, RadioCapabilities capabilities)
    {
        RadioText.Add(facts, "Tuning range", RadioText.Range(capabilities.MinFrequencyHz, capabilities.MaxFrequencyHz));
        RadioText.Add(facts, "Sample rate", RadioText.Hertz(capabilities.SampleRateHz) + "/s");
        RadioText.Add(facts, "Gain", capabilities.GainDb is null ? "automatic" : $"{capabilities.GainDb:0.#} dB");
        RadioText.Add(facts, "Clock correction", $"{capabilities.Ppm} ppm");
        if (capabilities.DirectSamplingBelow25MHz)
            RadioText.Add(facts, "Direct sampling", $"below {RadioText.Hertz(RadioLimits.DirectSamplingCutoffHz)}");
    }

    public static IRadioDevice Open(RadioDeviceInfo info, double sampleRateHz, double? gainDb, int ppm)
    {
        if (info.Driver.Equals("lime", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(info.OpenArgs))
                throw new RadioException($"No open arguments for {info.Name}.");
            return LimeDevice.Open(info.OpenArgs, sampleRateHz, gainDb, ppm);
        }

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

    private static bool SameRadio(RadioDeviceInfo existing, RadioDeviceInfo candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate.Serial) || string.IsNullOrWhiteSpace(existing.Serial))
            return false;
        return existing.Serial.Equals(candidate.Serial, StringComparison.OrdinalIgnoreCase)
            && existing.Driver.Equals(candidate.Driver, StringComparison.OrdinalIgnoreCase);
    }
}
