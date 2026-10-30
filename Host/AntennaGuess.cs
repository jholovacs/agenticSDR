namespace AgenticSdr.Host;

/// <summary>
/// How loud one spot on the dial was. FloorDb is a quiet part of that spectrum, not the strongest peak,
/// so one nearby station does not look like the antenna's natural frequency.
/// </summary>
public sealed record SignalStrength(double FrequencyHz, double FloorDb);

/// <summary>
/// Guesses a length from where the noise floor is strongest.
/// Levels below 32 MHz are the receiver's own front end, and the FM broadcast band is full of stations.
/// Those are set aside when the rest of the dial has a shape. The natural frequency is the service nearest that peak.
/// A vertical is a quarter-wave there, a horizontal wire a half-wave.
/// Strength cannot show compass direction, so a close call is called vertical and the other length is named in the note.
/// </summary>
public static class AntennaGuess
{
    /// <summary>
    /// Direct sampling ends at 24 MHz, and the tuner is unreliable for a few megahertz above that.
    /// Readings below this line are not compared with the rest of the dial.
    /// </summary>
    private const double TunerEdgeHz = 32_000_000;

    public static readonly double[] ProbeHz =
    [
        1_000_000, 3_800_000, 7_150_000, 14_200_000, 21_200_000, 27_200_000, 28_400_000,
        52_000_000, 98_000_000, 146_000_000, 162_550_000,
        300_000_000, 400_000_000, 420_000_000, 435_000_000, 446_000_000, 462_600_000,
        850_000_000
    ];

    public static IReadOnlyList<double> ProbesInRange(double minHz, double maxHz) =>
        ProbeHz.Where(hz => hz >= minHz && hz <= maxHz).ToList();

    public static bool TryFromFloors(IReadOnlyList<SignalStrength> readings, out AntennaSpec spec, out string note)
    {
        spec = new AntennaSpec(0, "vertical", null, "");
        note = "Not enough of the dial could be heard to guess an antenna.";
        if (readings.Count < 3)
            return false;

        double loudest = readings.Max(reading => reading.FloorDb);
        double quietest = readings.Min(reading => reading.FloorDb);
        if (loudest - quietest < 6)
        {
            note = "The noise floor was too even across the dial to guess an antenna.";
            return false;
        }

        SignalStrength fundamental = Choose(readings);
        double vertical = Length(fundamental.FrequencyHz, verticalAntenna: true);
        double horizontal = Length(fundamental.FrequencyHz, verticalAntenna: false);
        bool verticalOk = vertical is >= 0.05 and <= 200;
        bool horizontalOk = horizontal is >= 0.05 and <= 200;
        if (!verticalOk && !horizontalOk)
        {
            note = "The loud spot does not match an antenna between 5 cm and 200 m.";
            return false;
        }

        AntennaSpec? verticalSpec = verticalOk ? Spec(vertical, "vertical", null) : null;
        AntennaSpec? horizontalSpec = horizontalOk ? Spec(horizontal, "horizontal", null) : null;
        double verticalScore = verticalSpec is null ? double.NegativeInfinity : Score(verticalSpec, readings);
        double horizontalScore = horizontalSpec is null ? double.NegativeInfinity : Score(horizontalSpec, readings);
        bool useHorizontal = horizontalSpec is not null && horizontalScore > verticalScore + 2;
        spec = useHorizontal ? horizontalSpec! : verticalSpec ?? horizontalSpec!;
        string chosen = spec.Orientation == "vertical" ? "vertical" : "horizontal";
        note = $"Guessed from the noise floor, which was strongest near {fundamental.FrequencyHz / 1e6:0.00} MHz. " +
               (verticalOk ? $"A vertical antenna for that frequency is about {vertical:0.00} m. " : "") +
               (horizontalOk ? $"A horizontal wire would be about {horizontal:0.00} m. " : "") +
               "Strength does not show compass direction, so this guess is " + chosen + ".";
        return true;
    }

    public static double Length(double frequencyHz, bool verticalAntenna) =>
        verticalAntenna
            ? AntennaPlan.VelocityFactor * AntennaPlan.SpeedOfLight / (4 * frequencyHz)
            : AntennaPlan.VelocityFactor * AntennaPlan.SpeedOfLight / (2 * frequencyHz);

    /// <summary>
    /// Uses the tuner readings when they have a shape of their own.
    /// A short-wave antenna, whose strength is below the tuner edge, keeps the lowest loud spot on that part of the dial.
    /// </summary>
    private static SignalStrength Choose(IReadOnlyList<SignalStrength> readings)
    {
        List<SignalStrength> above = readings.Where(reading => reading.FrequencyHz >= TunerEdgeHz).ToList();
        List<SignalStrength> broadcast = above.Where(reading => IsBroadcastFm(reading.FrequencyHz)).ToList();
        List<SignalStrength> tuner = above.Where(reading => !IsBroadcastFm(reading.FrequencyHz)).ToList();
        bool broadcastStandsAlone = broadcast.Count > 0
            && (tuner.Count == 0 || broadcast.Max(reading => reading.FloorDb) > tuner.Max(reading => reading.FloorDb) + 8);
        List<SignalStrength> dial = broadcastStandsAlone ? above : tuner;
        if (dial.Count >= 2 && Spread(dial) >= 6)
            return NearestService(dial);

        return LowestLoud(readings);
    }

    /// <summary>The service nearest the loudest reading, among readings within 8 dB of it.</summary>
    private static SignalStrength NearestService(IReadOnlyList<SignalStrength> readings)
    {
        SignalStrength peak = readings.MaxBy(reading => reading.FloorDb)!;
        List<SignalStrength> body = readings
            .Where(reading => reading.FloorDb >= peak.FloorDb - 8)
            .ToList();
        List<SignalStrength> inBand = body.Where(reading => InServiceBand(reading.FrequencyHz)).ToList();
        if (inBand.Count == 0)
            return peak;

        return inBand
            .OrderBy(reading => Math.Abs(reading.FrequencyHz - peak.FrequencyHz))
            .ThenByDescending(reading => reading.FloorDb)
            .First();
    }

    private static SignalStrength LowestLoud(IReadOnlyList<SignalStrength> readings)
    {
        double loudest = readings.Max(reading => reading.FloorDb);
        return readings
            .Where(reading => reading.FloorDb >= loudest - 8)
            .OrderBy(reading => reading.FrequencyHz)
            .First();
    }

    private static bool InServiceBand(double frequencyHz) =>
        Region2BandPlan.Bands.Any(band => Region2BandPlan.Covers(band, frequencyHz));

    private static bool IsBroadcastFm(double frequencyHz) =>
        frequencyHz is >= 88_000_000 and <= 108_000_000;

    private static double Spread(IReadOnlyList<SignalStrength> readings) =>
        readings.Max(reading => reading.FloorDb) - readings.Min(reading => reading.FloorDb);

    private static AntennaSpec Spec(double meters, string orientation, double? azimuth)
    {
        double rounded = Math.Round(meters, 2);
        return new AntennaSpec(rounded, orientation, azimuth, $"{rounded:0.00} m");
    }

    private static double Score(AntennaSpec spec, IReadOnlyList<SignalStrength> readings)
    {
        AntennaAdvice advice = AntennaPlan.Advise(spec);
        double quietest = readings.Min(reading => reading.FloorDb);
        double score = 0;
        foreach (SignalStrength reading in readings)
        {
            bool fits = advice.Bands.Any(match =>
            {
                HamBand? band = Region2BandPlan.Find(match.BandId);
                return band is not null && Region2BandPlan.Covers(band, reading.FrequencyHz);
            });
            double weight = reading.FloorDb - quietest;
            if (fits)
                score += weight;
            else if (weight > 8)
                score -= weight * 0.25;
        }

        return score;
    }
}
