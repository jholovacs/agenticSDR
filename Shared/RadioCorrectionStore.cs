using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticSdr.Radio;

namespace AgenticSdr.Shared;

/// <summary>One radio's saved crystal correction in parts per million.</summary>
public sealed record RadioCorrection(
    int Ppm,
    string? Driver = null,
    string? Serial = null,
    string? Name = null);

/// <summary>Disk shape for <see cref="RadioCorrectionStore"/>.</summary>
public sealed record RadioCorrectionFile(
    Dictionary<string, RadioCorrection>? Radios = null);

/// <summary>
/// Persistent ppm corrections keyed by <see cref="RadioIdentity.Key"/>.
/// Host and mode desks share the same file so each physical radio keeps its own clock fix.
/// </summary>
public sealed class RadioCorrectionStore
{
    public const int MinPpm = -500;
    public const int MaxPpm = 500;

    readonly object _gate = new();
    readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };
    readonly string _path;

    public RadioCorrectionStore(string? path = null) => _path = path ?? SharedPaths.RadioCorrectionsJson();

    /// <summary>Saved ppm for this radio, or <paramref name="fallbackPpm"/> when none is stored yet.</summary>
    public int Resolve(RadioDeviceInfo info, int fallbackPpm)
    {
        ArgumentNullException.ThrowIfNull(info);
        lock (_gate)
        {
            Dictionary<string, RadioCorrection> radios = LoadUnlocked().Radios
                ?? new Dictionary<string, RadioCorrection>(StringComparer.OrdinalIgnoreCase);
            if (radios.TryGetValue(RadioIdentity.Key(info), out RadioCorrection? saved))
                return Clamp(saved.Ppm);
            return Clamp(fallbackPpm);
        }
    }

    public int? Get(RadioDeviceInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        lock (_gate)
        {
            Dictionary<string, RadioCorrection>? radios = LoadUnlocked().Radios;
            if (radios is not null && radios.TryGetValue(RadioIdentity.Key(info), out RadioCorrection? saved))
                return Clamp(saved.Ppm);
            return null;
        }
    }

    public RadioCorrection Set(RadioDeviceInfo info, int ppm)
    {
        ArgumentNullException.ThrowIfNull(info);
        int value = Clamp(ppm);
        lock (_gate)
        {
            RadioCorrectionFile current = LoadUnlocked();
            var radios = current.Radios is null
                ? new Dictionary<string, RadioCorrection>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, RadioCorrection>(current.Radios, StringComparer.OrdinalIgnoreCase);
            var entry = new RadioCorrection(value, info.Driver, info.Serial, info.Name);
            radios[RadioIdentity.Key(info)] = entry;
            WriteUnlocked(new RadioCorrectionFile(radios));
            return entry;
        }
    }

    public static int Clamp(int ppm)
    {
        if (ppm < MinPpm || ppm > MaxPpm)
            throw new ArgumentOutOfRangeException(nameof(ppm), ppm, $"PPM must be between {MinPpm} and {MaxPpm}.");
        return ppm;
    }

    RadioCorrectionFile LoadUnlocked()
    {
        try
        {
            if (!File.Exists(_path))
                return new RadioCorrectionFile();
            return JsonSerializer.Deserialize<RadioCorrectionFile>(File.ReadAllText(_path), _json)
                ?? new RadioCorrectionFile();
        }
        catch
        {
            return new RadioCorrectionFile();
        }
    }

    void WriteUnlocked(RadioCorrectionFile file)
    {
        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(_path, JsonSerializer.Serialize(file, _json));
    }
}
