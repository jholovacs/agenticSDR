using System.Text.Json;
using AgenticSdr.Shared;

namespace AgenticSdr.Privilege;

/// <summary>In-memory control operator for a mode desk, optionally restored from a JSON file.</summary>
public sealed class OperatorSession
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    readonly object _gate = new();
    readonly string? _path;
    OperatorIdentity? _operator;

    /// <param name="persistPath">
    /// Shared operator file. Null or blank keeps the session in memory only (tests).
    /// Mode desks pass <see cref="SharedPaths.OperatorJson"/>.
    /// </param>
    public OperatorSession(string? persistPath = null) =>
        _path = string.IsNullOrWhiteSpace(persistPath) ? null : persistPath;

    public OperatorIdentity? Current
    {
        get
        {
            lock (_gate)
                return _operator;
        }
    }

    public OperatorIdentity SignIn(string callSign, LicenseClass license, string? grid = null)
    {
        if (!CallSign.TryNormalize(callSign, out string display, out string baseCall))
            throw new ArgumentException("That is not a usable US amateur call sign.");

        string? square = string.IsNullOrWhiteSpace(grid) ? null : grid.Trim().ToUpperInvariant();
        var identity = new OperatorIdentity(display, baseCall, license, square);
        lock (_gate)
        {
            _operator = identity;
            PersistUnlocked();
        }

        return identity;
    }

    public void SignOut()
    {
        lock (_gate)
        {
            _operator = null;
            PersistUnlocked();
        }
    }

    public bool Restore()
    {
        // Prefer the newest readable file among the canonical path and legacy names
        // (station-operator.json / cw-operator.json) so an older test write cannot
        // hide the operator the user actually saved.
        OperatorIdentity? best = null;
        DateTime bestTime = DateTime.MinValue;
        foreach (string candidate in CandidatePaths())
        {
            if (!TryLoad(candidate, out OperatorIdentity identity))
                continue;

            DateTime written = File.GetLastWriteTimeUtc(candidate);
            if (best is not null && written < bestTime)
                continue;

            best = identity;
            bestTime = written;
        }

        if (best is null)
            return false;

        lock (_gate)
        {
            _operator = best;
            PersistUnlocked();
        }

        return true;
    }

    IEnumerable<string> CandidatePaths()
    {
        if (string.IsNullOrWhiteSpace(_path))
            yield break;

        yield return _path;

        // Legacy names only apply to the shared user-config operator file.
        if (!string.Equals(_path, SharedPaths.OperatorJson(), StringComparison.OrdinalIgnoreCase))
            yield break;

        string root = SharedPaths.UserConfigDirectory();
        yield return Path.Combine(root, "station-operator.json");
        yield return Path.Combine(root, "cw-operator.json");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgenticSdr",
            "cw-operator.json");
    }

    static bool TryLoad(string path, out OperatorIdentity identity)
    {
        identity = null!;
        if (!File.Exists(path))
            return false;

        try
        {
            SavedOperator? saved = JsonSerializer.Deserialize<SavedOperator>(File.ReadAllText(path), JsonOptions);
            if (saved is null || !AmateurWords.TryParseLicense(saved.License, out LicenseClass license))
                return false;
            if (!CallSign.TryNormalize(saved.CallSign, out string display, out string baseCall))
                return false;

            string? grid = string.IsNullOrWhiteSpace(saved.Grid) ? null : saved.Grid.Trim().ToUpperInvariant();
            identity = new OperatorIdentity(display, baseCall, license, grid);
            return true;
        }
        catch
        {
            return false;
        }
    }

    void PersistUnlocked()
    {
        if (string.IsNullOrWhiteSpace(_path))
            return;

        if (_operator is null)
        {
            try
            {
                if (File.Exists(_path))
                    File.Delete(_path);
            }
            catch
            {
                /* File may be locked by another process. */
            }

            // Only clear legacy names when this session owns the shared user-config file.
            if (string.Equals(_path, SharedPaths.OperatorJson(), StringComparison.OrdinalIgnoreCase))
            {
                foreach (string candidate in CandidatePaths())
                {
                    if (string.Equals(candidate, _path, StringComparison.OrdinalIgnoreCase))
                        continue;
                    try
                    {
                        if (File.Exists(candidate))
                            File.Delete(candidate);
                    }
                    catch
                    {
                        /* Other apps may still hold a legacy copy. */
                    }
                }
            }

            return;
        }

        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        // Store the enum name (Technician/General/Extra) so the panel select restores cleanly.
        var saved = new SavedOperator(
            _operator.CallSign,
            _operator.License.ToString(),
            _operator.Grid);
        File.WriteAllText(_path, JsonSerializer.Serialize(saved, JsonOptions));
    }

    sealed record SavedOperator(string CallSign, string License, string? Grid);
}
