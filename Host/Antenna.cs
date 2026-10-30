namespace AgenticSdr.Host;

/// <summary>
/// The antenna the user described. Length is stored in meters. Orientation is "vertical" or "horizontal".
/// AzimuthDegrees is the compass direction a horizontal wire runs, degrees clockwise from true north. Vertical antennas leave it empty.
/// </summary>
public sealed record AntennaSpec(double LengthMeters, string Orientation, double? AzimuthDegrees, string LengthText);

/// <summary>
/// One band this antenna can hear. Harmonic is 1 for the natural frequency, then 3, 5, or 7.
/// A value of 9 means a long wire that can hear local AM without being resonant there. Reason is the sentence shown on the page.
/// </summary>
public sealed record AntennaBandMatch(string BandId, string Name, int Harmonic, double ResonantHz, string Reason);

/// <summary>
/// The result of applying an antenna. Summary is the sentence on the page. Bands are the matches, best first.
/// Configured is false until the user has entered a length.
/// </summary>
public sealed record AntennaAdvice(
    bool Configured,
    double LengthMeters,
    string Orientation,
    double? AzimuthDegrees,
    double ResonantHz,
    string Summary,
    IReadOnlyList<AntennaBandMatch> Bands);

/// <summary>
/// Turns an approximate length and orientation into the bands that antenna can hear.
/// A vertical is a quarter-wave. A horizontal wire is a half-wave. Odd harmonics are included, and a wire of about 15 m or longer can also hear local AM.
/// </summary>
public static class AntennaPlan
{
    public const double SpeedOfLight = 299_792_458;
    /// <summary>A real wire is a little slower than light in free space. 0.95 is the factor used for every length on this page.</summary>
    public const double VelocityFactor = 0.95;
    private const double LocalAmLengthMeters = 15;

    public static AntennaSpec Parse(double length, string? unit, string? orientation, double? azimuthDegrees)
    {
        string units = string.IsNullOrWhiteSpace(unit) ? "m" : unit.Trim().ToLowerInvariant();
        double meters = units switch
        {
            "m" or "meter" or "meters" => length,
            "ft" or "foot" or "feet" => length * 0.3048,
            _ => throw new ArgumentException("Antenna unit must be m or ft.")
        };

        if (meters is < 0.05 or > 200)
            throw new ArgumentException("Antenna length must be between 5 cm and 200 m.");

        string pose = (orientation ?? "").Trim().ToLowerInvariant() switch
        {
            "vertical" or "v" => "vertical",
            "horizontal" or "h" => "horizontal",
            _ => throw new ArgumentException("Antenna orientation must be vertical or horizontal.")
        };

        double? azimuth = null;
        if (pose == "horizontal")
        {
            if (azimuthDegrees is null || double.IsNaN(azimuthDegrees.Value))
                throw new ArgumentException("A horizontal antenna needs a compass direction in degrees true.");
            double turn = azimuthDegrees.Value % 360;
            if (azimuthDegrees.Value is < -360 or > 360)
                throw new ArgumentException("Compass direction must be from 0 to 360 degrees true.");
            if (turn < 0)
                turn += 360;
            azimuth = turn;
        }

        string text = units is "ft" or "foot" or "feet"
            ? $"{length:0.##} ft"
            : $"{meters:0.##} m";
        return new AntennaSpec(meters, pose, azimuth, text);
    }

    public static AntennaAdvice Advise(AntennaSpec spec)
    {
        bool vertical = spec.Orientation == "vertical";
        double fundamental = vertical
            ? VelocityFactor * SpeedOfLight / (4 * spec.LengthMeters)
            : VelocityFactor * SpeedOfLight / (2 * spec.LengthMeters);

        var matches = new List<RankedMatch>();
        foreach (HamBand band in Region2BandPlan.Bands)
        {
            RankedMatch? best = null;
            foreach ((double startHz, double endHz) in Region2BandPlan.Coverage(band))
            {
                for (int harmonic = 1; harmonic <= 7; harmonic += 2)
                {
                    double resonant = harmonic * fundamental;
                    double nearest = Math.Clamp(resonant, startHz, endHz);
                    double fraction = Math.Abs(nearest - resonant) / resonant;
                    double tolerance = harmonic switch
                    {
                        1 => 0.15,
                        3 => 0.10,
                        _ => 0.08
                    };
                    if (fraction > tolerance)
                        continue;

                    double center = (startHz + endHz) / 2;
                    double centerDistance = Math.Abs(center - resonant) / resonant;
                    var candidate = new RankedMatch(band, harmonic, resonant, centerDistance, fraction);
                    if (best is null || candidate.Harmonic < best.Harmonic || (candidate.Harmonic == best.Harmonic && candidate.CenterDistance < best.CenterDistance))
                        best = candidate;
                }
            }

            if (best is not null)
                matches.Add(best);
        }

        if (spec.LengthMeters >= LocalAmLengthMeters
            && fundamental < 30_000_000
            && matches.All(match => match.Band.Id != "am-bc"))
        {
            HamBand am = Region2BandPlan.Find("am-bc")!;
            matches.Add(new RankedMatch(am, 9, 1_000_000, 1, 1));
        }

        matches.Sort(static (a, b) =>
        {
            int harmonic = a.Harmonic.CompareTo(b.Harmonic);
            return harmonic != 0 ? harmonic : a.CenterDistance.CompareTo(b.CenterDistance);
        });

        var bands = matches.Select(match => new AntennaBandMatch(
            match.Band.Id,
            match.Band.Name,
            match.Harmonic,
            match.ResonantHz,
            Reason(spec, match))).ToList();

        return new AntennaAdvice(
            true,
            spec.LengthMeters,
            spec.Orientation,
            spec.AzimuthDegrees,
            fundamental,
            Summary(spec, fundamental, bands),
            bands);
    }

    public static AntennaAdvice None() =>
        new(false, 0, "", null, 0, "No antenna is set, so every tunable band is listed the same way.", []);

    private static string Summary(AntennaSpec spec, double fundamental, IReadOnlyList<AntennaBandMatch> bands)
    {
        string kind = spec.Orientation == "vertical" ? "quarter-wave" : "half-wave";
        string aim = spec.Orientation == "horizontal" && spec.AzimuthDegrees is not null
            ? $" The wire runs toward {FormatAzimuth(spec.AzimuthDegrees.Value)}, so the strongest directions are broadside at {FormatAzimuth(spec.AzimuthDegrees.Value + 90)} and {FormatAzimuth(spec.AzimuthDegrees.Value + 270)}."
            : spec.Orientation == "horizontal"
                ? " Compass direction is not set."
                : "";
        string names = bands.Count == 0
            ? "No band in this plan is near that resonance."
            : "Proper bands: " + string.Join(", ", bands.Select(band => band.Name)) + ".";
        return $"{spec.LengthText} {spec.Orientation} is a {kind} near {FormatFrequency(fundamental)}.{aim} {names}";
    }

    private static string Reason(AntennaSpec spec, RankedMatch match)
    {
        if (match.Harmonic == 9)
            return "Long enough for strong local AM. " + Polarization(spec, match.Band);

        string harmonic = match.Harmonic switch
        {
            1 => spec.Orientation == "vertical" ? "Quarter-wave" : "Half-wave",
            3 => "Third harmonic",
            5 => "Fifth harmonic",
            _ => "Seventh harmonic"
        };
        return $"{harmonic} near {FormatFrequency(match.ResonantHz)}. {Polarization(spec, match.Band)}";
    }

    private static string Polarization(AntennaSpec spec, HamBand band)
    {
        bool hf = band.EndHz < 30_000_000;
        if (hf && spec.Orientation == "horizontal" && spec.AzimuthDegrees is null)
            return "HF polarization is mixed. Compass direction is not set, so broadside is unknown.";
        if (hf && spec.Orientation == "horizontal")
            return $"HF polarization is mixed. Broadside favors {FormatAzimuth(spec.AzimuthDegrees!.Value + 90)} and {FormatAzimuth(spec.AzimuthDegrees.Value + 270)}.";
        if (hf)
            return "HF polarization is mixed by the ionosphere.";

        bool verticalService = band.Id is "fm-bc" or "noaa" or "cb" or "gmrs" or "dmr"
            || band.Segments.All(segment => segment.PreferredMode is AgenticSdr.Dsp.RadioMode.Nbfm or AgenticSdr.Dsp.RadioMode.Wbfm);
        if (spec.Orientation == "vertical")
            return verticalService
                ? "Vertical polarization matches this service."
                : "Much of this band is horizontal; a vertical still hears the FM portion.";

        if (spec.AzimuthDegrees is null)
            return verticalService
                ? "Cross-polarized. This service is vertical, so a horizontal wire hears it more weakly. Compass direction is not set."
                : "Horizontal match. Compass direction is not set, so broadside is unknown.";
        return verticalService
            ? "Cross-polarized. This service is vertical, so a horizontal wire hears it more weakly."
            : $"Horizontal match. Broadside favors {FormatAzimuth(spec.AzimuthDegrees.Value + 90)} and {FormatAzimuth(spec.AzimuthDegrees.Value + 270)}.";
    }

    private static string FormatFrequency(double hz) =>
        hz >= 1_000_000 ? $"{hz / 1e6:0.00} MHz" : $"{hz / 1e3:0} kHz";

    private static string FormatAzimuth(double degrees)
    {
        double turn = ((degrees % 360) + 360) % 360;
        string cardinal = turn switch
        {
            >= 337.5 or < 22.5 => "N",
            < 67.5 => "NE",
            < 112.5 => "E",
            < 157.5 => "SE",
            < 202.5 => "S",
            < 247.5 => "SW",
            < 292.5 => "W",
            _ => "NW"
        };
        return $"{turn:0}° {cardinal}";
    }

    /// <summary>A band match before it is sorted. CenterDistance is how far the band's middle sits from the resonant frequency.</summary>
    private sealed record RankedMatch(HamBand Band, int Harmonic, double ResonantHz, double CenterDistance, double Fraction);
}
