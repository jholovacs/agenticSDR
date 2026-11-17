using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgenticSdr.Privilege;

/// <summary>
/// Shared UI chrome remembered across Host and every mode desk: fold open/closed, volume,
/// and CW dial defaults. Prompt chat history is intentionally not stored here.
/// </summary>
public sealed record UiPrefs(
    Dictionary<string, bool>? Folds = null,
    double? Volume = null,
    double? DialMhz = null,
    double? TxWatts = null,
    double? ToneHz = null);

/// <summary>Partial update for PUT /api/ui-prefs. Null fields leave the saved value alone.</summary>
public sealed record UiPrefsPatch(
    Dictionary<string, bool>? Folds = null,
    double? Volume = null,
    double? DialMhz = null,
    double? TxWatts = null,
    double? ToneHz = null);

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

            var next = new UiPrefs(
                folds.Count == 0 ? null : folds,
                volume,
                dial,
                watts,
                tone);
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
