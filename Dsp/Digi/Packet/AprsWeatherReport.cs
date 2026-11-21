namespace AgenticSdr.Dsp;

/// <summary>
/// One RF-heard APRS weather report with a map position.
/// Units match the APRS wire format: mph, °F, inches of rain, millibars.
/// </summary>
public sealed record AprsWeatherReport(
    string Callsign,
    double LatitudeDegrees,
    double LongitudeDegrees,
    int? WindDegrees,
    int? WindMph,
    int? GustMph,
    int? TemperatureF,
    int? HumidityPercent,
    double? RainLastHourIn,
    double? Rain24hIn,
    double? RainSinceMidnightIn,
    double? PressureMbar,
    string? Comment,
    string Raw);

/// <summary>Any uncompressed APRS position (used to place Ultimeter / later weather for the same call).</summary>
public sealed record AprsPositionFix(
    string Callsign,
    double LatitudeDegrees,
    double LongitudeDegrees,
    bool IsWeatherSymbol,
    string Raw);

/// <summary>Peet Bros Ultimeter metrics without a position (position comes from a prior fix).</summary>
public sealed record AprsUltimeterMetrics(
    string Callsign,
    int? WindDegrees,
    int? WindMph,
    int? GustMph,
    int? TemperatureF,
    int? HumidityPercent,
    double? RainSinceMidnightIn,
    double? PressureMbar,
    string Raw);
