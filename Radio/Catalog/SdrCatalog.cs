namespace AgenticSdr.Radio;

/// <summary>
/// A published USB radio family: the SoapySDR driver key, a short name, the models people buy, and the module to install.
/// </summary>
public sealed record SdrFamily(string Driver, string Name, string Radios, string Module);

/// <summary>
/// Names for published USB radios. The catalog is a label and an install hint.
/// A SoapySDR module that is installed but missing from this list still opens.
/// </summary>
public static class SdrCatalog
{
    public static IReadOnlyList<SdrFamily> Families { get; } =
    [
        new("rtlsdr", "RTL-SDR", "NESDR SMArt, RTL-SDR Blog V3 and V4, Nooelec NESDR, and other RTL2832U dongles", "SoapyRTLSDR or the built-in librtlsdr"),
        new("airspy", "Airspy", "Airspy R2 and Airspy Mini", "SoapyAirspy"),
        new("airspyhf", "Airspy HF+", "Airspy HF+ and HF+ Discovery", "SoapyAirspyHF"),
        new("hackrf", "HackRF", "HackRF One, HackRF Jawbreaker, and rad1o", "SoapyHackRF"),
        new("bladerf", "bladeRF", "bladeRF and bladeRF 2.0 micro", "SoapyBladeRF"),
        new("lime", "LimeSDR", "LimeSDR USB, LimeSDR Mini, and LimeSDR Mini 2.0", "LimeSuiteNG, receive only, or SoapyLMS7"),
        new("plutosdr", "ADALM-Pluto", "ADALM-Pluto and Pluto+", "SoapyPlutoSDR"),
        new("sdrplay", "SDRplay", "RSP1, RSP1A, RSP2, RSPduo, RSPdx, and RSPdx-R2", "SoapySDRPlay"),
        new("uhd", "USRP", "USRP B200, B210, B200mini, and B205mini", "SoapyUHD"),
        new("fcdpp", "FUNcube", "FUNcube Dongle Pro+", "SoapyFCDPP"),
        new("sddc", "RX-888", "RX-888, RX-888 MKII, and other SDDC receivers", "SoapySDR SDDC"),
        new("rfspace", "RFSpace", "SDR-IQ, NetSDR, and CloudIQ", "SoapyRFSpace"),
        new("afedri", "Afedri", "Afedri USB SDR", "SoapyAfedri"),
        new("xtrx", "XTRX", "Fairwaves XTRX", "SoapyXTRX"),
        new("perseus", "Perseus", "Microtelecom Perseus", "SoapyPerseus")
    ];

    public static string DisplayName(string? driver, string? label)
    {
        if (!string.IsNullOrWhiteSpace(label)
            && !label.Equals(driver, StringComparison.OrdinalIgnoreCase))
            return label;

        SdrFamily? family = Find(driver);
        return family?.Name ?? (string.IsNullOrWhiteSpace(label) ? driver ?? "SDR" : label);
    }

    public static SdrFamily? Find(string? driver)
    {
        if (string.IsNullOrWhiteSpace(driver))
            return null;
        return Families.FirstOrDefault(family => family.Driver.Equals(driver, StringComparison.OrdinalIgnoreCase));
    }
}
