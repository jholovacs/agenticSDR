namespace AgenticSdr.Privilege;

/// <summary>
/// Parse and summarize antenna settings for any desk. Host still runs the full band-match planner.
/// </summary>
public static class AntennaSettings
{
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

    public static object ToAdvice(AntennaSpec? spec)
    {
        if (spec is null)
        {
            return new
            {
                configured = false,
                lengthMeters = 0.0,
                orientation = "",
                azimuthDegrees = (double?)null,
                summary = "No antenna is set.",
                bands = Array.Empty<object>()
            };
        }

        string aim = spec.Orientation == "horizontal" && spec.AzimuthDegrees is not null
            ? $" Wire axis {spec.AzimuthDegrees:0}° true."
            : "";
        return new
        {
            configured = true,
            lengthMeters = spec.LengthMeters,
            orientation = spec.Orientation,
            azimuthDegrees = spec.AzimuthDegrees,
            summary = $"{spec.LengthText} {spec.Orientation}.{aim} Shared across Agentic SDR apps on this machine.",
            bands = Array.Empty<object>()
        };
    }
}
