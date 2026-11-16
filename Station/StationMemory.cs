using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgenticSdr.Station;

/// <summary>
/// Writes the signed-in operator so the next start of this desk comes back to the same call sign, class, and grid.
/// Signing out removes the file. A draft is not saved, and nothing here transmits.
/// </summary>
public sealed class StationMemory
{
    readonly object gate = new();
    readonly JsonSerializerOptions json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };
    readonly string path;

    public StationMemory(string path) => this.path = path;

    public bool Restore(StationDesk desk)
    {
        SavedOperator? saved = Read();
        if (saved is null || !StationWords.TryParseLicense(saved.License, out LicenseClass license))
            return false;

        try
        {
            desk.SignIn(saved.CallSign, license, saved.Grid);
            return desk.CopyOperator() != null;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public void Save(OperatorIdentity op)
    {
        var saved = new SavedOperator(op.CallSign, op.License.ToString(), op.Grid);
        string body = JsonSerializer.Serialize(saved, json);
        lock (gate)
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(path, body);
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    SavedOperator? Read()
    {
        lock (gate)
        {
            try
            {
                if (!File.Exists(path))
                    return null;
                return JsonSerializer.Deserialize<SavedOperator>(File.ReadAllText(path), json);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return null;
            }
        }
    }

    sealed record SavedOperator(string CallSign, string License, string? Grid);
}
