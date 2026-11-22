namespace AgenticSdr.Host;

/// <summary>
/// One RF-heard station for the live map (camelCase JSON on /api/status).
/// Weather metrics are optional; digimode pins often only have call + Maidenhead center.
/// Source is aprs / weather / ft8 / js8 / wspr / olivia / …
/// Grid is the Maidenhead square when the fix came from a 4- or 6-character locator.
/// </summary>
public sealed record AprsWeatherStation(
    string Callsign,
    double Latitude,
    double Longitude,
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
    DateTimeOffset HeardAt,
    string Raw,
    bool IsWeather = true,
    string Source = "aprs",
    string? Grid = null);
