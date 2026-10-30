namespace AgenticSdr.Host;

/// <summary>
/// Settings from Host/appsettings.json under "Sdr".
/// Sample rate, gain, and clock correction (Ppm) apply when a radio is opened.
/// Latitude, longitude, or a Maidenhead grid let a nearby repeater name replace the band-plan label. The region selects which amateur plan is fair to mention; only region 2 is loaded.
/// </summary>
public sealed class SdrOptions
{
    public double SampleRateHz { get; set; } = 2_400_000;
    public double GainDb { get; set; } = 28;
    public bool AutoGain { get; set; }
    public int Ppm { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string? Grid { get; set; }
    public int ItuRegion { get; set; } = 2;
}
