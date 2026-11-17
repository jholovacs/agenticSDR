using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgenticSdr.Privilege;

/// <summary>
/// The antenna the user described. Length is stored in meters. Orientation is "vertical" or "horizontal".
/// AzimuthDegrees is the compass direction a horizontal wire runs, degrees clockwise from true north.
/// </summary>
public sealed record AntennaSpec(double LengthMeters, string Orientation, double? AzimuthDegrees, string LengthText);

/// <summary>
/// Remembers the last antenna so Host and mode desks restore the same settings.
/// </summary>
public sealed class AntennaStore
{
    readonly object _gate = new();
    readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };
    string _path;

    public AntennaStore(string? path = null) => _path = path ?? SharedPaths.AntennaJson();

    public AntennaSpec? Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path))
                    return null;
                string text = File.ReadAllText(_path);
                if (string.IsNullOrWhiteSpace(text))
                    return null;
                SavedAntenna? saved = JsonSerializer.Deserialize<SavedAntenna>(text, _json);
                if (saved is null || saved.LengthMeters is < 0.05 or > 200)
                    return null;
                string orientation = (saved.Orientation ?? "").Trim().ToLowerInvariant();
                if (orientation is not ("vertical" or "horizontal"))
                    return null;
                double? azimuth = orientation == "horizontal" ? saved.AzimuthDegrees : null;
                if (orientation == "horizontal" && azimuth is null)
                    return null;
                string lengthText = string.IsNullOrWhiteSpace(saved.LengthText)
                    ? $"{saved.LengthMeters:0.##} m"
                    : saved.LengthText.Trim();
                return new AntennaSpec(saved.LengthMeters, orientation, azimuth, lengthText);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                return null;
            }
        }
    }

    public void Save(AntennaSpec spec)
    {
        lock (_gate)
        {
            var saved = new SavedAntenna(spec.LengthMeters, spec.Orientation, spec.AzimuthDegrees, spec.LengthText);
            string json = JsonSerializer.Serialize(saved, _json);
            Write(json);
        }
    }

    void Write(string json)
    {
        try
        {
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(_path, json);
        }
        catch (UnauthorizedAccessException) when (!string.Equals(_path, SharedPaths.AntennaJson(), StringComparison.OrdinalIgnoreCase))
        {
            _path = SharedPaths.AntennaJson();
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(_path, json);
        }
    }

    sealed record SavedAntenna(
        double LengthMeters,
        string? Orientation,
        double? AzimuthDegrees,
        string? LengthText);
}
