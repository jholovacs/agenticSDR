namespace AgenticSdr.Host;

/// <summary>One RF-heard APRS station for the live map (camelCase JSON on /api/status).</summary>
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
    bool IsWeather = true);
