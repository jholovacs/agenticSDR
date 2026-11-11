using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AgenticSdr.Host;

/// <summary>
/// One cached callsign card. Name and city come from HamDB when present;
/// country / continent / region come from HamQTH’s free DXCC table so DX works without a login.
/// </summary>
public sealed record CallsignInfo(
    string Call,
    bool Found,
    string? Name,
    string? City,
    string? State,
    string? Country,
    string? Grid,
    string? Continent,
    string? DxccDetail,
    string Source,
    DateTimeOffset FetchedUtc)
{
    /// <summary>Short place/name for digi assisted text, or null when there is nothing useful to show.</summary>
    public string? Label
    {
        get
        {
            if (!Found)
                return null;

            var bits = new List<string>();
            string? person = DisplayName;
            if (!string.IsNullOrWhiteSpace(person))
                bits.Add(person);

            string? place = Place;
            if (!string.IsNullOrWhiteSpace(place) && !bits.Exists(bit => bit.Contains(place, StringComparison.OrdinalIgnoreCase)))
                bits.Add(place);

            if (bits.Count == 0 && !string.IsNullOrWhiteSpace(DxccDetail))
                bits.Add(DxccDetail);
            else if (bits.Count == 0 && !string.IsNullOrWhiteSpace(Country))
                bits.Add(Country);

            return bits.Count == 0 ? null : string.Join(", ", bits);
        }
    }

    private string? DisplayName => string.IsNullOrWhiteSpace(Name) ? null : Name.Trim();

    private string? Place
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(City))
            {
                string city = City.Trim();
                parts.Add(city == city.ToUpperInvariant()
                    ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(city.ToLowerInvariant())
                    : city);
            }

            if (!string.IsNullOrWhiteSpace(State))
                parts.Add(State.Trim().ToUpperInvariant());
            if (!string.IsNullOrWhiteSpace(Country)
                && !Country.Equals("United States", StringComparison.OrdinalIgnoreCase)
                && !Country.Equals("USA", StringComparison.OrdinalIgnoreCase)
                && !Country.Equals("Canada", StringComparison.OrdinalIgnoreCase))
                parts.Add(Country.Trim());
            else if (parts.Count == 0 && !string.IsNullOrWhiteSpace(Country))
                parts.Add(Country.Trim());
            else if (parts.Count > 0
                && !string.IsNullOrWhiteSpace(Country)
                && Country.Equals("Canada", StringComparison.OrdinalIgnoreCase))
                parts.Add("Canada");

            // Prefer a plain country name inside assisted-text parentheses (avoid “Japan (Japan (Kanto))”).
            if (parts.Count == 0 && !string.IsNullOrWhiteSpace(Country))
                parts.Add(Country.Trim());
            else if (parts.Count == 0 && !string.IsNullOrWhiteSpace(DxccDetail))
                parts.Add(DxccDetail.Trim());
            else if (parts.Count == 0 && !string.IsNullOrWhiteSpace(Continent))
                parts.Add(Continent.Trim());
            return parts.Count == 0 ? null : string.Join(" ", parts);
        }
    }

    /// <summary>Richer one-liner for a transcript note when a lookup first resolves.</summary>
    public string? Note
    {
        get
        {
            if (!Found)
                return null;
            var bits = new List<string>();
            if (!string.IsNullOrWhiteSpace(DisplayName))
                bits.Add(DisplayName);
            if (!string.IsNullOrWhiteSpace(City) || !string.IsNullOrWhiteSpace(State))
            {
                string? place = Place;
                if (!string.IsNullOrWhiteSpace(place))
                    bits.Add(place);
            }
            else if (!string.IsNullOrWhiteSpace(DxccDetail))
                bits.Add(DxccDetail);
            else if (!string.IsNullOrWhiteSpace(Country))
                bits.Add(Country);
            return bits.Count == 0 ? Label : string.Join(", ", bits);
        }
    }
}

/// <summary>
/// Free, non-blocking callsign references for digimode text.
/// HamDB supplies licensee details where it has them; HamQTH DXCC fills in the DX entity for every prefix.
/// Results are kept in memory and on disk so the audio loop never waits on the network.
/// </summary>
public sealed partial class CallsignLookup
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    private readonly HttpClient _http;
    private readonly ILogger<CallsignLookup> _log;
    private readonly string _cachePath;
    private readonly bool _enabled;
    private readonly ConcurrentDictionary<string, CallsignInfo> _memory = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _inflight = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _diskGate = new();
    private readonly TimeSpan _positiveTtl = TimeSpan.FromDays(30);
    private readonly TimeSpan _negativeTtl = TimeSpan.FromDays(1);

    public CallsignLookup(HttpClient http, ILogger<CallsignLookup> log, string cachePath, bool enabled = true)
    {
        _http = http;
        _log = log;
        _cachePath = cachePath;
        _enabled = enabled;
        LoadDisk();
    }

    public bool Enabled => _enabled;

    /// <summary>Raised on a thread-pool thread when a fresh positive lookup lands (not when read from cache).</summary>
    public event Action<CallsignInfo>? Resolved;

    /// <summary>Cache-only. Never touches the network.</summary>
    public CallsignInfo? TryGet(string? callsign)
    {
        if (!_enabled || !TryNormalize(callsign, out string call))
            return null;
        if (!_memory.TryGetValue(call, out CallsignInfo? info))
            return null;
        if (IsStale(info))
            return null;
        return info.Found ? info : null;
    }

    /// <summary>Queue a background fetch if the cache is empty or stale. Returns immediately.</summary>
    public void Prefetch(string? callsign)
    {
        if (!_enabled || !TryNormalize(callsign, out string call))
            return;
        if (_memory.TryGetValue(call, out CallsignInfo? cached) && !IsStale(cached))
            return;
        if (!_inflight.TryAdd(call, 0))
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                CallsignInfo info = await LookupNetworkAsync(call, CancellationToken.None).ConfigureAwait(false);
                _memory[call] = info;
                SaveDiskEntry(info);
                if (info.Found)
                    Resolved?.Invoke(info);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
            {
                _log.LogDebug(ex, "Callsign lookup failed for {Call}.", call);
            }
            finally
            {
                _inflight.TryRemove(call, out _);
            }
        });
    }

    /// <summary>Pull every plausible callsign out of digimode text and prefetch each one.</summary>
    public void PrefetchFromText(string? text)
    {
        if (!_enabled || string.IsNullOrWhiteSpace(text))
            return;
        foreach (string call in ExtractCallsigns(text))
            Prefetch(call);
    }

    /// <summary>Rewrite assisted digi lines so known callsigns carry a short label from the cache.</summary>
    public string EnrichText(string text)
    {
        if (!_enabled || string.IsNullOrWhiteSpace(text))
            return text;

        PrefetchFromText(text);
        string enriched = text;
        foreach (string call in ExtractCallsigns(text))
        {
            CallsignInfo? info = TryGet(call);
            string? label = info?.Label;
            if (string.IsNullOrWhiteSpace(label))
                continue;
            // Skip tokens already followed by a gloss in parentheses.
            string pattern = $@"\b{Regex.Escape(call)}\b(?!\s*\()";
            enriched = Regex.Replace(
                enriched,
                pattern,
                $"{call} ({label})",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        return enriched;
    }

    public static IReadOnlyList<string> ExtractCallsigns(string text)
    {
        var found = new List<string>();
        foreach (Match match in CallTokenRegex().Matches(text))
        {
            if (!TryNormalize(match.Value, out string call))
                continue;
            if (IsReserved(call) || LooksLikeGrid(call))
                continue;
            if (!found.Exists(existing => existing.Equals(call, StringComparison.OrdinalIgnoreCase)))
                found.Add(call);
        }

        return found;
    }

    public static bool TryNormalize(string? raw, out string call)
    {
        call = "";
        if (string.IsNullOrWhiteSpace(raw))
            return false;
        string text = raw.Trim().Trim('<', '>').ToUpperInvariant();
        int slash = text.IndexOf('/');
        if (slash > 0)
        {
            string left = text[..slash];
            string right = text[(slash + 1)..];
            // Portable /P /R /MM keep the home call; prefix/call (VP2/W1AW) keeps the right side when it looks like a call.
            text = right is "P" or "R" or "M" or "MM" or "AM" || right.Length <= 2
                ? left
                : LooksLikeCall(right) ? right : left;
        }

        if (!LooksLikeCall(text) || IsReserved(text) || LooksLikeGrid(text))
            return false;
        call = text;
        return true;
    }

    public static CallsignInfo? ParseHamDb(string call, string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("hamdb", out JsonElement root))
            return null;
        if (!root.TryGetProperty("messages", out JsonElement messages)
            || !messages.TryGetProperty("status", out JsonElement status)
            || !string.Equals(status.GetString(), "OK", StringComparison.OrdinalIgnoreCase))
        {
            return new CallsignInfo(call, false, null, null, null, null, null, null, null, "hamdb", DateTimeOffset.UtcNow);
        }

        if (!root.TryGetProperty("callsign", out JsonElement card))
            return null;

        string? name = JoinName(Read(card, "fname"), Read(card, "mi"), Read(card, "name"));
        string? city = Clean(Read(card, "addr2"));
        string? state = Clean(Read(card, "state"));
        string? country = Clean(Read(card, "country"));
        string? grid = Clean(Read(card, "grid"));
        bool found = !string.IsNullOrWhiteSpace(name)
            || !string.IsNullOrWhiteSpace(city)
            || !string.IsNullOrWhiteSpace(country)
            || !string.IsNullOrWhiteSpace(grid);
        return new CallsignInfo(call, found, name, city, state, country, grid, null, null, "hamdb", DateTimeOffset.UtcNow);
    }

    public static CallsignInfo? ParseDxcc(string call, string json, CallsignInfo? prior)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        string? detail = Clean(root.TryGetProperty("details", out JsonElement details) ? details.GetString() : null);
        string? continent = Clean(root.TryGetProperty("continent", out JsonElement cont) ? cont.GetString() : null);
        string? country = null;
        if (root.TryGetProperty("entity", out JsonElement entity)
            && entity.TryGetProperty("name", out JsonElement entityName))
            country = Clean(entityName.GetString());

        if (string.IsNullOrWhiteSpace(detail) && string.IsNullOrWhiteSpace(country))
        {
            return prior ?? new CallsignInfo(call, false, null, null, null, null, null, null, null, "hamqth-dxcc", DateTimeOffset.UtcNow);
        }

        if (prior is null)
        {
            return new CallsignInfo(
                call,
                true,
                null,
                null,
                null,
                country,
                null,
                continent,
                detail,
                "hamqth-dxcc",
                DateTimeOffset.UtcNow);
        }

        bool found = prior.Found || !string.IsNullOrWhiteSpace(country) || !string.IsNullOrWhiteSpace(detail);
        string source = prior.Found && prior.Source.Contains("hamdb", StringComparison.Ordinal)
            ? prior.Source + "+hamqth-dxcc"
            : string.IsNullOrWhiteSpace(prior.Source) || prior.Source == "none"
                ? "hamqth-dxcc"
                : prior.Source + "+hamqth-dxcc";
        return prior with
        {
            Found = found,
            Country = string.IsNullOrWhiteSpace(prior.Country) ? country : prior.Country,
            Continent = string.IsNullOrWhiteSpace(prior.Continent) ? continent : prior.Continent,
            DxccDetail = string.IsNullOrWhiteSpace(prior.DxccDetail) ? detail : prior.DxccDetail,
            Source = source,
            FetchedUtc = DateTimeOffset.UtcNow
        };
    }

    private async Task<CallsignInfo> LookupNetworkAsync(string call, CancellationToken cancellationToken)
    {
        CallsignInfo? hamdb = null;
        try
        {
            string url = $"https://api.hamdb.org/{Uri.EscapeDataString(call)}/json/agenticsdr";
            using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                hamdb = ParseHamDb(call, body);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _log.LogDebug(ex, "HamDB lookup failed for {Call}.", call);
        }

        CallsignInfo? merged = hamdb;
        try
        {
            string url = $"https://www.hamqth.com/api/v2/dxcc?callsign={Uri.EscapeDataString(call)}";
            using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                merged = ParseDxcc(call, body, hamdb);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _log.LogDebug(ex, "HamQTH DXCC lookup failed for {Call}.", call);
        }

        return merged ?? new CallsignInfo(call, false, null, null, null, null, null, null, null, "none", DateTimeOffset.UtcNow);
    }

    private bool IsStale(CallsignInfo info)
    {
        TimeSpan age = DateTimeOffset.UtcNow - info.FetchedUtc;
        return info.Found ? age > _positiveTtl : age > _negativeTtl;
    }

    private void LoadDisk()
    {
        if (!_enabled || !File.Exists(_cachePath))
            return;
        try
        {
            string json = File.ReadAllText(_cachePath);
            Dictionary<string, CallsignInfo>? entries = JsonSerializer.Deserialize<Dictionary<string, CallsignInfo>>(json, JsonOptions);
            if (entries is null)
                return;
            foreach ((string key, CallsignInfo info) in entries)
            {
                if (IsStale(info))
                    continue;
                _memory[key] = info;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _log.LogDebug(ex, "Callsign cache was not loaded from {Path}.", _cachePath);
        }
    }

    private void SaveDiskEntry(CallsignInfo info)
    {
        lock (_diskGate)
        {
            try
            {
                string? directory = Path.GetDirectoryName(_cachePath);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                Dictionary<string, CallsignInfo> all = new(StringComparer.OrdinalIgnoreCase);
                if (File.Exists(_cachePath))
                {
                    string existing = File.ReadAllText(_cachePath);
                    Dictionary<string, CallsignInfo>? parsed =
                        JsonSerializer.Deserialize<Dictionary<string, CallsignInfo>>(existing, JsonOptions);
                    if (parsed is not null)
                    {
                        foreach ((string key, CallsignInfo value) in parsed)
                        {
                            if (!IsStale(value))
                                all[key] = value;
                        }
                    }
                }

                foreach ((string key, CallsignInfo value) in _memory)
                {
                    if (!IsStale(value))
                        all[key] = value;
                }

                all[info.Call] = info;
                File.WriteAllText(_cachePath, JsonSerializer.Serialize(all, JsonOptions));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                _log.LogDebug(ex, "Callsign cache was not saved to {Path}.", _cachePath);
            }
        }
    }

    private static string? JoinName(string? first, string? middle, string? last)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(first) && !IsMissing(first))
            parts.Add(first.Trim());
        if (!string.IsNullOrWhiteSpace(middle) && !IsMissing(middle))
            parts.Add(middle.Trim());
        if (!string.IsNullOrWhiteSpace(last) && !IsMissing(last))
            parts.Add(last.Trim());
        return parts.Count == 0 ? null : string.Join(' ', parts);
    }

    private static string? Read(JsonElement card, string name) =>
        card.TryGetProperty(name, out JsonElement value) ? value.GetString() : null;

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) || IsMissing(value) ? null : value.Trim();

    private static bool IsMissing(string? value) =>
        string.IsNullOrWhiteSpace(value)
        || value.Equals("NOT_FOUND", StringComparison.OrdinalIgnoreCase)
        || value.Equals("null", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeCall(string text) =>
        text.Length is >= 3 and <= 10
        && text.Any(char.IsDigit)
        && text.Any(char.IsLetter)
        && text.All(ch => char.IsAsciiLetterOrDigit(ch));

    private static bool LooksLikeGrid(string text) =>
        text.Length is 4 or 6
        && char.IsAsciiLetter(text[0]) && char.IsAsciiLetter(text[1])
        && char.IsDigit(text[2]) && char.IsDigit(text[3])
        && (text.Length == 4
            || (char.IsAsciiLetter(text[4]) && char.IsAsciiLetter(text[5])));

    private static bool IsReserved(string call) => call is
        "CQ" or "DX" or "DE" or "QRZ" or "TU" or "RR73" or "RRR" or "73" or "RR";

    [GeneratedRegex(@"\b[A-Za-z0-9]{1,3}[0-9][A-Za-z0-9]{1,5}\b", RegexOptions.CultureInvariant)]
    private static partial Regex CallTokenRegex();
}
