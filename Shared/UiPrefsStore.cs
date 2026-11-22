using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgenticSdr.Shared;

/// <summary>
/// Shared UI chrome remembered across Host and every mode desk: fold open/closed, volume,
/// and CW dial defaults. Prompt chat history is intentionally not stored here.
/// </summary>
public sealed record UiPrefs(
    Dictionary<string, bool>? Folds = null,
    double? Volume = null,
    double? DialMhz = null,
    double? TxWatts = null,
    double? ToneHz = null,
    double? HomeLatitude = null,
    double? HomeLongitude = null,
    string? HomeGrid = null);

/// <summary>Partial update for PUT /api/ui-prefs. Null fields leave the saved value alone.</summary>
public sealed record UiPrefsPatch(
    Dictionary<string, bool>? Folds = null,
    double? Volume = null,
    double? DialMhz = null,
    double? TxWatts = null,
    double? ToneHz = null,
    double? HomeLatitude = null,
    double? HomeLongitude = null,
    string? HomeGrid = null,
    bool ClearHome = false);

/// <summary>Reads and merges ui-prefs.json under <see cref="SharedPaths"/>.</summary>
public sealed class UiPrefsStore
{
    readonly object _gate = new();
    readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };
    readonly string _path;

    public UiPrefsStore(string? path = null) => _path = path ?? SharedPaths.UiPrefsJson();

    public UiPrefs Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path))
                    return new UiPrefs();
                return JsonSerializer.Deserialize<UiPrefs>(File.ReadAllText(_path), _json) ?? new UiPrefs();
            }
            catch
            {
                return new UiPrefs();
            }
        }
    }

    public UiPrefs Patch(UiPrefsPatch patch)
    {
        lock (_gate)
        {
            UiPrefs current = LoadUnlocked();
            var folds = current.Folds is null
                ? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, bool>(current.Folds, StringComparer.OrdinalIgnoreCase);

            if (patch.Folds is not null)
            {
                foreach ((string key, bool open) in patch.Folds)
                {
                    if (string.IsNullOrWhiteSpace(key))
                        continue;
                    folds[key.Trim()] = open;
                }
            }

            double? volume = patch.Volume ?? current.Volume;
            if (volume is < 0 or > 100)
                throw new ArgumentException("Volume must be between 0 and 100.");

            double? dial = patch.DialMhz ?? current.DialMhz;
            if (dial is <= 0 or > 3000)
                throw new ArgumentException("Dial MHz must be a usable radio frequency.");

            double? watts = patch.TxWatts ?? current.TxWatts;
            if (watts is <= 0 or > 1500)
                throw new ArgumentException("Power must be between 0 and 1500 W.");

            double? tone = patch.ToneHz ?? current.ToneHz;
            if (tone is < 300 or > 1200)
                throw new ArgumentException("Tone must be between 300 and 1200 Hz.");

            double? homeLat = current.HomeLatitude;
            double? homeLon = current.HomeLongitude;
            string? homeGrid = current.HomeGrid;
            if (patch.ClearHome)
            {
                homeLat = null;
                homeLon = null;
                homeGrid = null;
            }
            else
            {
                bool setCoords = patch.HomeLatitude is not null || patch.HomeLongitude is not null;
                bool setGrid = patch.HomeGrid is not null;

                if (setGrid)
                {
                    string grid = patch.HomeGrid!.Trim().ToUpperInvariant();
                    if (grid.Length == 0)
                        homeGrid = null;
                    else if (grid.Length is not (4 or 6)
                        || grid[0] is < 'A' or > 'R' || grid[1] is < 'A' or > 'R'
                        || !char.IsDigit(grid[2]) || !char.IsDigit(grid[3])
                        || (grid.Length == 6 && (grid[4] is < 'A' or > 'X' || grid[5] is < 'A' or > 'X')))
                        throw new ArgumentException("Home grid must be a 4- or 6-character Maidenhead locator.");
                    else
                    {
                        homeGrid = grid;
                        // Grid alone replaces prior coordinates so TryLocation uses the new square.
                        if (!setCoords)
                        {
                            homeLat = null;
                            homeLon = null;
                        }
                    }
                }

                if (setCoords)
                {
                    homeLat = patch.HomeLatitude ?? homeLat;
                    homeLon = patch.HomeLongitude ?? homeLon;
                }

                if (homeLat is < -90 or > 90)
                    throw new ArgumentException("Home latitude must be between -90 and 90.");
                if (homeLon is < -180 or > 180)
                    throw new ArgumentException("Home longitude must be between -180 and 180.");
                if ((homeLat is null) != (homeLon is null))
                    throw new ArgumentException("Home latitude and longitude must be set together.");
            }

            var next = new UiPrefs(
                folds.Count == 0 ? null : folds,
                volume,
                dial,
                watts,
                tone,
                homeLat,
                homeLon,
                homeGrid);
            WriteUnlocked(next);
            return next;
        }
    }

    UiPrefs LoadUnlocked()
    {
        try
        {
            if (!File.Exists(_path))
                return new UiPrefs();
            return JsonSerializer.Deserialize<UiPrefs>(File.ReadAllText(_path), _json) ?? new UiPrefs();
        }
        catch
        {
            return new UiPrefs();
        }
    }

    void WriteUnlocked(UiPrefs prefs)
    {
        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(_path, JsonSerializer.Serialize(prefs, _json));
    }
}
