using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticSdr.Radio;

namespace AgenticSdr.Shared;

/// <summary>Last radio the operator opened. Shared by Host and mode desks.</summary>
public sealed record RadioPreference(
    string? Serial,
    string? Driver,
    string? Name,
    int Index,
    bool SimulateTransmit = false);

/// <summary>Persists the preferred USB radio across apps on this machine.</summary>
public sealed class RadioPreferenceStore
{
    readonly object _gate = new();
    readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };
    readonly string _path;

    public RadioPreferenceStore(string? path = null) => _path = path ?? SharedPaths.RadioJson();

    public RadioPreference? Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path))
                    return null;
                return JsonSerializer.Deserialize<RadioPreference>(File.ReadAllText(_path), _json);
            }
            catch
            {
                return null;
            }
        }
    }

    public void Save(RadioDeviceInfo info)
    {
        RadioPreference? previous = Load();
        Save(new RadioPreference(
            info.Serial,
            info.Driver,
            info.Name,
            info.Index,
            previous?.SimulateTransmit ?? false));
    }

    public void Save(RadioPreference preference)
    {
        lock (_gate)
        {
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(_path, JsonSerializer.Serialize(preference, _json));
        }
    }

    public bool SimulateTransmit() => Load()?.SimulateTransmit ?? false;

    public void SetSimulateTransmit(bool enabled)
    {
        RadioPreference? previous = Load();
        Save(new RadioPreference(
            previous?.Serial,
            previous?.Driver,
            previous?.Name,
            previous?.Index ?? 0,
            enabled));
    }

    /// <summary>Pick the remembered radio from a live device list, else the saved index when still valid.</summary>
    public int ResolveIndex(IReadOnlyList<RadioDeviceInfo> devices)
    {
        if (devices.Count == 0)
            return 0;

        RadioPreference? saved = Load();
        if (saved is null)
            return 0;

        if (!string.IsNullOrWhiteSpace(saved.Serial))
        {
            for (int i = 0; i < devices.Count; i++)
            {
                if (string.Equals(devices[i].Serial, saved.Serial, StringComparison.OrdinalIgnoreCase)
                    && (string.IsNullOrWhiteSpace(saved.Driver)
                        || string.Equals(devices[i].Driver, saved.Driver, StringComparison.OrdinalIgnoreCase)))
                    return i;
            }
        }

        if (saved.Index >= 0 && saved.Index < devices.Count)
            return saved.Index;

        return 0;
    }
}
