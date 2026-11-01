using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace AgenticSdr.Host;

/// <summary>The schedule and the Explore or Camp choice, saved so the agent and the page share them.</summary>
public sealed record CampView(
    string Mode,
    bool Paused,
    bool YieldToSchedule,
    int Squelch,
    int Revision,
    string? Note,
    CampEntry? Now,
    CampEntry? Due,
    CampEntry? Active,
    IReadOnlyList<CampEntry> Entries);

/// <summary>
/// The camp schedule. Timed entries must not overlap. One open entry, with no clock time, is allowed
/// and runs only when nothing timed is on. An explicit camp frequency starts right away; whether a
/// timed entry can take the radio away from it is a saved choice.
/// </summary>
public sealed class CampBook
{
    private readonly object _gate = new();
    private readonly Channel<int> _changes = Channel.CreateBounded<int>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false
    });
    private readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    private string _path;
    private string _mode = "explore";
    private string? _pausedKey;
    private bool _yieldToSchedule;
    private int _squelch = 50;
    private string? _followId;
    private string? _holdId;
    private CampEntry? _now;
    private List<CampEntry> _entries = [];
    private int _revision;

    public CampBook(string path)
    {
        _path = path;
        Load();
    }

    public string Mode
    {
        get { lock (_gate) return _mode; }
    }

    public int Revision
    {
        get { lock (_gate) return _revision; }
    }

    public bool Paused
    {
        get { lock (_gate) return _pausedKey is not null; }
    }

    public int Squelch
    {
        get { lock (_gate) return _squelch; }
    }

    public void SetSquelch(int value)
    {
        lock (_gate)
        {
            _squelch = Math.Clamp(value, 0, 100);
            Save();
        }

        Poke();
    }

    public void Poke() => _changes.Writer.TryWrite(1);

    public async Task WaitAsync(TimeSpan idle, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(idle);
        try
        {
            await _changes.Reader.ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
    }

    public CampView View(DateTime local)
    {
        lock (_gate)
            return Snapshot(local);
    }

    public void SetMode(string? mode)
    {
        string next = mode?.Trim().ToLowerInvariant() switch
        {
            "camp" => "camp",
            "explore" => "explore",
            _ => throw new ArgumentException("Mode is explore or camp.")
        };
        lock (_gate)
        {
            _mode = next;
            if (next == "camp")
                _pausedKey = null;
            Save();
        }

        Poke();
    }

    public CampEntry Add(CampDraft draft) => Edit(entries =>
    {
        CampEntry entry = CampSchedule.Normalize(draft, NewId(), allowOpen: true);
        RejectOverlap(entry, entries);
        entries.Add(entry);
        return entry;
    });

    public CampEntry Update(string id, CampDraft draft) => Edit(entries =>
    {
        int index = entries.FindIndex(entry => entry.Id == id);
        if (index < 0)
            throw new ArgumentException("That schedule entry is gone.");
        CampEntry entry = CampSchedule.Normalize(draft, id, allowOpen: true);
        RejectOverlap(entry, entries);
        entries[index] = entry;
        return entry;
    });

    public void Remove(string id)
    {
        lock (_gate)
        {
            if (_entries.RemoveAll(entry => entry.Id == id) == 0)
                throw new ArgumentException("That schedule entry is gone.");
            Save();
        }

        Poke();
    }

    public CampView CampNow(CampDraft draft, DateTime local)
    {
        CampEntry entry = CampSchedule.Normalize(draft, "now", allowOpen: true);
        entry = entry with { Start = null, End = null, Days = null };
        lock (_gate)
        {
            _mode = "camp";
            _now = entry;
            _pausedKey = null;
            _followId = null;
            _holdId = null;
            Save();
            CampView view = Snapshot(local);
            Poke();
            return view;
        }
    }

    public void ClearNow()
    {
        lock (_gate)
        {
            _now = null;
            _followId = null;
            _holdId = null;
            Save();
        }

        Poke();
    }

    public void SetYieldToSchedule(bool yield)
    {
        lock (_gate)
        {
            _yieldToSchedule = yield;
            _followId = null;
            _holdId = null;
            Save();
        }

        Poke();
    }

    /// <summary>While camping on an explicit frequency, take the timed entry that is on right now.</summary>
    public CampView FollowSchedule(DateTime local)
    {
        lock (_gate)
        {
            CampEntry? timed = CampSchedule.ActiveTimed(_entries, local);
            if (timed is null)
                throw new ArgumentException("Nothing on the schedule is due right now.");
            if (_now is null)
                throw new ArgumentException("Camp on a frequency first, then choose whether the schedule may take over.");
            _followId = timed.Id;
            _holdId = null;
            _revision++;
            CampView view = Snapshot(local);
            Poke();
            return view;
        }
    }

    /// <summary>While camping on an explicit frequency, keep it through the timed entry that is on right now.</summary>
    public CampView IgnoreSchedule(DateTime local)
    {
        lock (_gate)
        {
            CampEntry? timed = CampSchedule.ActiveTimed(_entries, local);
            if (timed is null)
                throw new ArgumentException("Nothing on the schedule is due right now.");
            if (_now is null)
                throw new ArgumentException("Camp on a frequency first, then choose whether to ignore the schedule.");
            _holdId = timed.Id;
            _followId = null;
            _revision++;
            CampView view = Snapshot(local);
            Poke();
            return view;
        }
    }

    public void Pause(DateTime local)
    {
        lock (_gate)
        {
            _pausedKey = Pick(local)?.Key ?? "*";
            _revision++;
        }

        Poke();
    }

    public CampRequest? Desired(DateTime local)
    {
        lock (_gate)
        {
            if (_mode != "camp")
                return null;
            CampRequest? chosen = Pick(local);
            if (_pausedKey is null)
                return chosen;
            if (chosen is null || chosen.Key == _pausedKey)
                return null;
            _pausedKey = null;
            _revision++;
            return chosen;
        }
    }

    private CampEntry Edit(Func<List<CampEntry>, CampEntry> edit)
    {
        CampEntry saved;
        lock (_gate)
        {
            var copy = _entries.ToList();
            saved = edit(copy);
            _entries = copy;
            Save();
        }

        Poke();
        return saved;
    }

    private CampRequest? Pick(DateTime local)
    {
        CampEntry? timed = CampSchedule.ActiveTimed(_entries, local);
        ForgetStaleOverrides(timed);
        if (_now is not null)
        {
            if (timed is not null && TakesSchedule(timed))
                return CampRequest.From(timed);
            return CampRequest.From(_now);
        }

        if (timed is not null)
            return CampRequest.From(timed);
        CampEntry? standing = _entries.FirstOrDefault(entry => entry.Start is null);
        return standing is null ? null : CampRequest.From(standing);
    }

    private bool TakesSchedule(CampEntry timed)
    {
        if (_holdId == timed.Id)
            return false;
        if (_followId == timed.Id)
            return true;
        return _yieldToSchedule;
    }

    private void ForgetStaleOverrides(CampEntry? timed)
    {
        if (_followId is not null && timed?.Id != _followId)
            _followId = null;
        if (_holdId is not null && timed?.Id != _holdId)
            _holdId = null;
    }

    private CampView Snapshot(DateTime local)
    {
        CampEntry? timed = CampSchedule.ActiveTimed(_entries, local);
        ForgetStaleOverrides(timed);
        CampEntry? standing = _entries.FirstOrDefault(entry => entry.Start is null);
        CampEntry? active = null;
        if (_now is not null)
            active = timed is not null && TakesSchedule(timed) ? timed : _now;
        else
            active = timed ?? standing;
        CampRequest? chosen = active is null ? null : CampRequest.From(active);
        bool blocked = _mode == "camp" && _pausedKey is not null && (chosen is null || chosen.Key == _pausedKey);
        CampEntry? due = _now is not null && timed is not null && !TakesSchedule(timed) ? timed : null;
        string? note = null;
        if (blocked)
            note = "Camping is paused. Press Camp, or leave Camp and come back, to resume the schedule.";
        else if (_now is not null && timed is not null && TakesSchedule(timed))
            note = $"On {CampSchedule.Describe(timed)}. Returns to {CampSchedule.Describe(_now)} when that window ends.";
        else if (due is not null)
            note = $"{CampSchedule.Describe(due)} is on the schedule. Staying on {CampSchedule.Describe(_now!)}.";
        else if (_now is not null)
            note = $"Camping on {CampSchedule.Describe(_now)}.";
        return new CampView(
            _mode,
            _pausedKey is not null,
            _yieldToSchedule,
            _squelch,
            _revision,
            note,
            _now,
            due,
            blocked ? null : active,
            _entries.ToArray());
    }

    private static void RejectOverlap(CampEntry entry, IReadOnlyList<CampEntry> entries)
    {
        CampEntry? other = entries.FirstOrDefault(item => CampSchedule.Overlaps(entry, item));
        if (other is not null)
            throw new ArgumentException($"That overlaps {CampSchedule.Describe(other)}.");
    }

    private void Save()
    {
        _revision++;
        var saved = new Saved(_mode, _yieldToSchedule, _squelch, _now, _entries);
        try
        {
            Write(saved);
        }
        catch (UnauthorizedAccessException) when (_path != AppPaths.UserSchedule())
        {
            _path = AppPaths.UserSchedule();
            Write(saved);
        }
    }

    private void Write(Saved saved)
    {
        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(_path, JsonSerializer.Serialize(saved, _json));
    }

    private void Load()
    {
        if (!File.Exists(_path))
            return;
        try
        {
            Saved? saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(_path), _json);
            if (saved is null)
                return;
            _mode = saved.Mode == "camp" ? "camp" : "explore";
            _yieldToSchedule = saved.YieldToSchedule;
            _squelch = Math.Clamp(saved.Squelch ?? 50, 0, 100);
            _now = saved.Now;
            _entries = saved.Entries ?? [];
        }
        catch (JsonException)
        {
        }
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..12];

    private sealed record Saved(string? Mode, bool YieldToSchedule, int? Squelch, CampEntry? Now, List<CampEntry>? Entries);
}
