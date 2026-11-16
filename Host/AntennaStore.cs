using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgenticSdr.Host;

/// <summary>
/// Remembers the last antenna the user applied or guessed so the next host start can restore it.
/// </summary>
public sealed class AntennaStore
{
    private readonly object _gate = new();
    private readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };
    private string _path;

    public AntennaStore(string path)
    {
        _path = path;
    }

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
            try
            {
                string? directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);
                File.WriteAllText(_path, json);
            }
            catch (UnauthorizedAccessException) when (_path != AppPaths.UserAntenna())
            {
                _path = AppPaths.UserAntenna();
                string? directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);
                File.WriteAllText(_path, json);
            }
        }
    }

    private sealed record SavedAntenna(
        double LengthMeters,
        string? Orientation,
        double? AzimuthDegrees,
        string? LengthText);
}
