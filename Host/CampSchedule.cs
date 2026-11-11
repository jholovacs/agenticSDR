using System.Globalization;
using AgenticSdr.Dsp;

namespace AgenticSdr.Host;

/// <summary>
/// One thing to camp on. Start and End are local times, HH:mm. Both empty means the radio may use
/// this entry whenever no timed entry is on. Days empty means every day. An end earlier than the start
/// continues past midnight.
/// </summary>
public sealed record CampEntry(
    string Id,
    string? Label,
    double FrequencyHz,
    string Mode,
    string? Start,
    string? End,
    int[]? Days,
    int? ColorCode,
    int? Slot,
    int? Talkgroup);

/// <summary>What the camp listener should tune. Key ignores the label, so renaming an entry does not retune.</summary>
public sealed record CampRequest(
    string Key,
    string? EntryId,
    string? Label,
    double FrequencyHz,
    string Mode,
    DmrFilter Filter)
{
    public static CampRequest From(CampEntry entry)
    {
        string freq = entry.FrequencyHz.ToString("0.######", CultureInfo.InvariantCulture);
        var filter = new DmrFilter(entry.ColorCode, entry.Slot, entry.Talkgroup);
        string key = $"{freq}|{entry.Mode}|{filter.ColorCode}|{filter.Slot}|{filter.Talkgroup}";
        return new CampRequest(key, entry.Id, entry.Label, entry.FrequencyHz, entry.Mode, filter);
    }
}

/// <summary>The tune the page is hearing while camp mode holds the radio.</summary>
public sealed record CampTune(
    string? EntryId,
    string? Label,
    double FrequencyHz,
    string Mode,
    bool Activity,
    int SampleRate);

/// <summary>Checks that schedule windows do not ask the one radio to be in two places at once.</summary>
public static class CampSchedule
{
    private static readonly string[] DayNames = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

    public static CampEntry Normalize(CampDraft draft, string id, bool allowOpen)
    {
        if (draft.FrequencyHz is < 1_000 or > 6_000_000_000)
            throw new ArgumentException("Enter a frequency the radio can tune, in hertz.");

        RadioMode parsed;
        try
        {
            parsed = ModeClassifier.Parse(draft.Mode);
        }
        catch (ArgumentException)
        {
            throw new ArgumentException("Camp needs a specific mode, such as nbfm, am, usb, or wbfm.");
        }

        if (parsed == RadioMode.Unknown)
            throw new ArgumentException("Camp needs a specific mode, such as nbfm, am, usb, or wbfm.");

        string? start = string.IsNullOrWhiteSpace(draft.Start) ? null : Clock(draft.Start);
        string? end = string.IsNullOrWhiteSpace(draft.End) ? null : Clock(draft.End);
        if ((start is null) != (end is null))
            throw new ArgumentException("Set both a start and an end, or leave both empty.");
        if (start is not null && start == end)
            throw new ArgumentException("The end time has to be different from the start.");
        if (!allowOpen && start is null)
            throw new ArgumentException("Set the time this entry should use the radio.");

        int[]? days = CleanDays(draft.Days);
        string? label = string.IsNullOrWhiteSpace(draft.Label) ? null : draft.Label.Trim();
        if (label is { Length: > 80 })
            throw new ArgumentException("The label is too long.");

        int? color = draft.ColorCode;
        int? slot = draft.Slot;
        int? talkgroup = draft.Talkgroup;
        if (parsed != RadioMode.Dmr)
        {
            color = null;
            slot = null;
            talkgroup = null;
        }
        else
        {
            if (color is < 0 or > 15)
                throw new ArgumentException("Color code is 0 through 15, or empty for any.");
            if (slot is not null && slot is not (1 or 2))
                throw new ArgumentException("Timeslot is 1, 2, or empty for any.");
            if (talkgroup is < 0 or > 16_777_215)
                throw new ArgumentException("Talkgroup has to be a radio id, or empty for any.");
        }

        return new CampEntry(id, label, draft.FrequencyHz, Canonical(parsed), start, end, days, color, slot, talkgroup);
    }

    public static int[]? ParseDays(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        string trimmed = text.Trim();
        if (trimmed.Equals("every", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("all", StringComparison.OrdinalIgnoreCase))
            return null;
        if (trimmed.Equals("weekdays", StringComparison.OrdinalIgnoreCase))
            return [1, 2, 3, 4, 5];
        if (trimmed.Equals("weekends", StringComparison.OrdinalIgnoreCase))
            return [0, 6];

        var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["sun"] = 0, ["sunday"] = 0, ["0"] = 0,
            ["mon"] = 1, ["monday"] = 1, ["1"] = 1,
            ["tue"] = 2, ["tues"] = 2, ["tuesday"] = 2, ["2"] = 2,
            ["wed"] = 3, ["wednesday"] = 3, ["3"] = 3,
            ["thu"] = 4, ["thur"] = 4, ["thurs"] = 4, ["thursday"] = 4, ["4"] = 4,
            ["fri"] = 5, ["friday"] = 5, ["5"] = 5,
            ["sat"] = 6, ["saturday"] = 6, ["6"] = 6
        };
        var days = new List<int>();
        foreach (string token in trimmed.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!names.TryGetValue(token, out int day))
                throw new ArgumentException($"Unknown day '{token}'. Use sun, mon, tue, wed, thu, fri, sat, weekdays, or weekends.");
            days.Add(day);
        }

        return CleanDays(days);
    }

    public static bool Overlaps(CampEntry left, CampEntry right)
    {
        if (!string.IsNullOrEmpty(left.Id) && left.Id == right.Id)
            return false;
        bool openLeft = left.Start is null;
        bool openRight = right.Start is null;
        if (openLeft || openRight)
            return openLeft && openRight;

        foreach ((int start, int end) in Spans(left))
        {
            foreach ((int otherStart, int otherEnd) in Spans(right))
            {
                if (start < otherEnd && otherStart < end)
                    return true;
            }
        }

        return false;
    }

    public static CampEntry? ActiveTimed(IReadOnlyList<CampEntry> entries, DateTime local)
    {
        int minute = local.Hour * 60 + local.Minute;
        int day = (int)local.DayOfWeek;
        foreach (CampEntry entry in entries)
        {
            if (entry.Start is not null && Covers(entry, day, minute))
                return entry;
        }

        return null;
    }

    public static string Describe(CampEntry entry)
    {
        string who = string.IsNullOrWhiteSpace(entry.Label)
            ? $"{entry.FrequencyHz / 1e6:0.######} MHz {entry.Mode}"
            : entry.Label!;
        if (entry.Start is null)
            return $"{who}, any time";
        string when = $"{entry.Start}–{entry.End}";
        if (entry.Days is { Length: > 0 and < 7 })
            when += " " + string.Join(" ", entry.Days.Select(day => DayNames[day]));
        return $"{who} ({when})";
    }

    public static string Canonical(RadioMode mode) => mode switch
    {
        RadioMode.Nbfm => "nbfm",
        RadioMode.Wbfm => "wbfm",
        RadioMode.Am => "am",
        RadioMode.Usb => "usb",
        RadioMode.Lsb => "lsb",
        RadioMode.Cw => "cw",
        RadioMode.Ft8 => "ft8",
        RadioMode.Js8 => "js8",
        RadioMode.Olivia => "olivia",
        RadioMode.Jtty => "jtty",
        RadioMode.Varac => "varac",
        RadioMode.Dmr => "dmr",
        RadioMode.M17 => "m17",
        RadioMode.Packet => "packet",
        RadioMode.Raw => "raw",
        _ => "nbfm"
    };

    private static bool Covers(CampEntry entry, int day, int minute)
    {
        foreach ((int start, int end) in Spans(entry))
        {
            int at = day * 1440 + minute;
            if (at >= start && at < end)
                return true;
        }

        return false;
    }

    private static IEnumerable<(int Start, int End)> Spans(CampEntry entry)
    {
        int start = Minutes(entry.Start!);
        int end = Minutes(entry.End!);
        IEnumerable<int> days = entry.Days is { Length: > 0 } ? entry.Days : [0, 1, 2, 3, 4, 5, 6];
        foreach (int day in days.Distinct())
        {
            int origin = day * 1440;
            if (start < end)
            {
                yield return (origin + start, origin + end);
                continue;
            }

            yield return (origin + start, origin + 1440);
            int next = ((day + 1) % 7) * 1440;
            yield return (next, next + end);
        }
    }

    private static int[]? CleanDays(IReadOnlyList<int>? days)
    {
        if (days is null || days.Count == 0)
            return null;
        if (days.Any(day => day is < 0 or > 6))
            throw new ArgumentException("Days are numbered 0 (Sunday) through 6 (Saturday).");
        int[] cleaned = days.Distinct().OrderBy(day => day).ToArray();
        return cleaned.Length is 0 or 7 ? null : cleaned;
    }

    private static string Clock(string text)
    {
        string trimmed = text.Trim();
        string[] parts = trimmed.Split(':');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int hour)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int minute)
            || hour is < 0 or > 23
            || minute is < 0 or > 59)
            throw new ArgumentException("Times use 24-hour HH:mm, such as 18:30.");
        return $"{hour:00}:{minute:00}";
    }

    private static int Minutes(string clock)
    {
        string[] parts = clock.Split(':');
        return int.Parse(parts[0], CultureInfo.InvariantCulture) * 60 + int.Parse(parts[1], CultureInfo.InvariantCulture);
    }
}

/// <summary>Fields for a new or edited schedule row. Times and days are optional.</summary>
public sealed class CampDraft
{
    public string? Label { get; set; }
    public double FrequencyHz { get; set; }
    public string? Mode { get; set; }
    public string? Start { get; set; }
    public string? End { get; set; }
    public int[]? Days { get; set; }
    public int? ColorCode { get; set; }
    public int? Slot { get; set; }
    public int? Talkgroup { get; set; }
}
