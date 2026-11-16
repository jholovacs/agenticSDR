using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AgenticSdr.Station;

/// <summary>How the transmit frequency sits relative to the published repeater output.</summary>
public enum RepeaterShift
{
    /// <summary>Transmit above the output by the offset.</summary>
    Plus,
    /// <summary>Transmit below the output by the offset.</summary>
    Minus,
    /// <summary>Transmit and receive on the same frequency (no repeater).</summary>
    Simplex,
    /// <summary>Transmit on a typed input frequency that is not a standard offset.</summary>
    Split
}

/// <summary>How the radio opens the repeater, or quiets the receiver.</summary>
public enum RepeaterToneMode
{
    None,
    Ctcss,
    Dcs
}

/// <summary>
/// FM phone through a repeater, or simplex FM phone using the same form.
/// Output is what you listen to. Input is what you transmit, and that is what Part 97 checks.
/// Encode tone opens the repeater. Decode tone is optional receive squelch.
/// </summary>
public sealed record RepeaterAccess(
    double OutputFrequencyHz,
    double InputFrequencyHz,
    RepeaterShift Shift,
    double? OffsetHz,
    RepeaterToneMode EncodeMode,
    double? EncodeCtcssHz,
    string? EncodeDcs,
    RepeaterToneMode DecodeMode,
    double? DecodeCtcssHz,
    string? DecodeDcs,
    bool Narrow,
    string? RepeaterCallSign,
    string? Name,
    string? City,
    string? Notes)
{
    public string Summary
    {
        get
        {
            var bits = new List<string>
            {
                $"out {FormatMhz(OutputFrequencyHz)}",
                $"in {FormatMhz(InputFrequencyHz)}",
                ShiftLabel(Shift)
            };
            if (OffsetHz is { } offset && Shift is RepeaterShift.Plus or RepeaterShift.Minus)
                bits.Add($"{offset / 1e3:0.###} kHz");
            bits.Add(ToneLabel("encode", EncodeMode, EncodeCtcssHz, EncodeDcs));
            if (DecodeMode != RepeaterToneMode.None)
                bits.Add(ToneLabel("decode", DecodeMode, DecodeCtcssHz, DecodeDcs));
            bits.Add(Narrow ? "narrow" : "wide");
            if (!string.IsNullOrWhiteSpace(RepeaterCallSign))
                bits.Add(RepeaterCallSign);
            if (!string.IsNullOrWhiteSpace(Name))
                bits.Add(Name!);
            if (!string.IsNullOrWhiteSpace(City))
                bits.Add(City!);
            return string.Join(" · ", bits);
        }
    }

    static string FormatMhz(double hz)
    {
        double mhz = hz / 1e6;
        int digits = mhz >= 100 ? 3 : 4;
        return mhz.ToString($"0.{new string('#', digits)}", CultureInfo.InvariantCulture) + " MHz";
    }

    static string ShiftLabel(RepeaterShift shift) => shift switch
    {
        RepeaterShift.Plus => "+",
        RepeaterShift.Minus => "−",
        RepeaterShift.Simplex => "simplex",
        RepeaterShift.Split => "split",
        _ => shift.ToString()
    };

    static string ToneLabel(string role, RepeaterToneMode mode, double? ctcss, string? dcs) => mode switch
    {
        RepeaterToneMode.None => $"{role} none",
        RepeaterToneMode.Ctcss => $"{role} {ctcss:0.0} Hz",
        RepeaterToneMode.Dcs => $"{role} DCS {dcs}",
        _ => role
    };
}

/// <summary>Body fields the page or an agent can send when describing a repeater.</summary>
public sealed record RepeaterBody(
    double? OutputFrequencyHz,
    double? InputFrequencyHz,
    string? Shift,
    double? OffsetHz,
    string? EncodeMode,
    double? EncodeCtcssHz,
    string? EncodeDcs,
    string? DecodeMode,
    double? DecodeCtcssHz,
    string? DecodeDcs,
    bool? Narrow,
    string? RepeaterCallSign,
    string? Name,
    string? City,
    string? Notes);

/// <summary>
/// Parses repeater phone settings, lists the usual CTCSS and DCS codes, and picks a standard offset for the band.
/// This does not look up a directory and it does not transmit.
/// </summary>
public static partial class StationRepeater
{
    public static readonly double[] CtcssHz =
    [
        67.0, 69.3, 71.9, 74.4, 77.0, 79.7, 82.5, 85.4, 88.5, 91.5, 94.8, 97.4,
        100.0, 103.5, 107.2, 110.9, 114.8, 118.8, 123.0, 127.3, 131.8, 136.5, 141.3, 146.2,
        151.4, 156.7, 159.8, 162.2, 165.5, 167.9, 171.3, 173.8, 177.3, 179.9, 183.5, 186.2,
        189.9, 192.8, 196.6, 199.5, 203.5, 206.5, 210.7, 218.1, 225.7, 229.1, 233.6, 241.8,
        250.3, 254.1
    ];

    public static readonly string[] DcsCodes =
    [
        "023", "025", "026", "031", "032", "036", "043", "047", "051", "053", "054", "065", "071", "072", "073", "074",
        "114", "115", "116", "122", "125", "131", "132", "134", "143", "145", "152", "155", "156", "162", "165", "172",
        "174", "205", "212", "223", "225", "226", "243", "244", "245", "246", "251", "252", "255", "261", "263", "265",
        "266", "271", "274", "306", "311", "315", "325", "331", "332", "343", "346", "351", "356", "364", "365", "371",
        "411", "412", "413", "423", "431", "432", "445", "446", "452", "454", "455", "462", "464", "465", "466", "503",
        "506", "516", "523", "526", "532", "546", "565", "606", "612", "624", "627", "631", "632", "654", "662", "664",
        "703", "712", "723", "731", "732", "734", "743", "754"
    ];

    public static object Catalog() => new
    {
        shifts = new[] { "plus", "minus", "simplex", "split" },
        toneModes = new[] { "none", "ctcss", "dcs" },
        ctcssHz = CtcssHz,
        dcsCodes = DcsCodes,
        standardOffsetsHz = new[]
        {
            new { band = "10 m", fromHz = 29_000_000.0, toHz = 29_700_000.0, offsetHz = 100_000.0 },
            new { band = "6 m", fromHz = 51_000_000.0, toHz = 54_000_000.0, offsetHz = 1_000_000.0 },
            new { band = "2 m", fromHz = 144_000_000.0, toHz = 148_000_000.0, offsetHz = 600_000.0 },
            new { band = "1.25 m", fromHz = 222_000_000.0, toHz = 225_000_000.0, offsetHz = 1_600_000.0 },
            new { band = "70 cm", fromHz = 420_000_000.0, toHz = 450_000_000.0, offsetHz = 5_000_000.0 },
            new { band = "33 cm", fromHz = 902_000_000.0, toHz = 928_000_000.0, offsetHz = 12_000_000.0 },
            new { band = "23 cm", fromHz = 1_240_000_000.0, toHz = 1_300_000_000.0, offsetHz = 12_000_000.0 }
        },
        note = "Output is the published listen frequency. Input is what you transmit. Encode opens the repeater. Decode is optional receive squelch."
    };

    public static bool TryStandardOffset(double outputFrequencyHz, out double offsetHz, out string? band)
    {
        foreach ((double from, double to, double offset, string name) in StandardOffsets())
        {
            if (outputFrequencyHz >= from && outputFrequencyHz <= to)
            {
                offsetHz = offset;
                band = name;
                return true;
            }
        }

        offsetHz = 0;
        band = null;
        return false;
    }

    public static RepeaterAccess Parse(RepeaterBody body)
    {
        if (body.OutputFrequencyHz is null || !double.IsFinite(body.OutputFrequencyHz.Value) || body.OutputFrequencyHz <= 0)
            throw new ArgumentException("Enter the repeater output frequency in hertz (the published listen frequency).");

        double output = Math.Round(body.OutputFrequencyHz.Value);
        if (!TryParseShift(body.Shift, out RepeaterShift shift))
            throw new ArgumentException("Shift has to be plus, minus, simplex, or split.");

        double input;
        double? offset = null;
        if (shift == RepeaterShift.Simplex)
        {
            input = output;
        }
        else if (shift == RepeaterShift.Split)
        {
            if (body.InputFrequencyHz is null || !double.IsFinite(body.InputFrequencyHz.Value) || body.InputFrequencyHz <= 0)
                throw new ArgumentException("A split repeater needs an input frequency in hertz.");
            input = Math.Round(body.InputFrequencyHz.Value);
            if (Math.Abs(input - output) < 1)
                throw new ArgumentException("Split needs an input frequency different from the output.");
            offset = Math.Abs(input - output);
        }
        else
        {
            double amount = body.OffsetHz is { } typed && double.IsFinite(typed) && typed > 0
                ? typed
                : TryStandardOffset(output, out double standard, out _)
                    ? standard
                    : throw new ArgumentException("Enter an offset in hertz, or use a band that has a standard offset.");
            offset = Math.Round(amount);
            input = shift == RepeaterShift.Plus ? output + offset.Value : output - offset.Value;
            if (input <= 0)
                throw new ArgumentException("That offset puts the transmit frequency at or below zero.");
        }

        (RepeaterToneMode encodeMode, double? encodeCtcss, string? encodeDcs) = ParseTone(
            "Encode",
            body.EncodeMode,
            body.EncodeCtcssHz,
            body.EncodeDcs,
            requiredWhenPresent: false);
        (RepeaterToneMode decodeMode, double? decodeCtcss, string? decodeDcs) = ParseTone(
            "Decode",
            body.DecodeMode,
            body.DecodeCtcssHz,
            body.DecodeDcs,
            requiredWhenPresent: false);

        return new RepeaterAccess(
            output,
            input,
            shift,
            offset,
            encodeMode,
            encodeCtcss,
            encodeDcs,
            decodeMode,
            decodeCtcss,
            decodeDcs,
            body.Narrow ?? true,
            CleanOptional(body.RepeaterCallSign, 20),
            CleanOptional(body.Name, 80),
            CleanOptional(body.City, 80),
            CleanOptional(body.Notes, 400));
    }

    public static PrivilegeDecision Evaluate(LicenseClass license, RepeaterAccess repeater, double powerWatts)
    {
        PrivilegeDecision tx = Part97.Evaluate(license, StationMode.Nbfm, repeater.InputFrequencyHz, powerWatts);
        if (!tx.WithinLicense)
            return tx with
            {
                Reason = "Transmit (input) frequency: " + tx.Reason
            };

        PrivilegeDecision rx = Part97.Evaluate(license, StationMode.Nbfm, repeater.OutputFrequencyHz, powerWatts);
        string? caution = JoinCaution(
            tx.Caution,
            rx.WithinLicense
                ? "Repeater phone: you transmit on the input and listen on the output. Use only on a coordinated machine you are allowed to access."
                : "You may listen on that output, but Part 97 would not let you transmit there. Confirm the published pair before keying.",
            repeater.EncodeMode == RepeaterToneMode.None && repeater.Shift != RepeaterShift.Simplex
                ? "No encode tone is set. Many repeaters need CTCSS or DCS to open."
                : null);

        return tx with
        {
            Reason = $"FM phone through the repeater is within license on the {tx.Band} input at {repeater.InputFrequencyHz / 1e6:0.####} MHz"
                     + (tx.MaxWatts is { } max ? $", up to {max} W PEP." : "."),
            Caution = caution
        };
    }

    public static bool TryParseShift(string? text, out RepeaterShift shift)
    {
        shift = default;
        string key = Compact(text);
        switch (key)
        {
            case "plus":
            case "+":
            case "positive":
            case "up":
                shift = RepeaterShift.Plus;
                return true;
            case "minus":
            case "-":
            case "−":
            case "negative":
            case "down":
                shift = RepeaterShift.Minus;
                return true;
            case "simplex":
            case "simple":
            case "none":
            case "0":
                shift = RepeaterShift.Simplex;
                return true;
            case "split":
            case "odd":
            case "custom":
                shift = RepeaterShift.Split;
                return true;
            default:
                return false;
        }
    }

    public static bool TryParseToneMode(string? text, out RepeaterToneMode mode)
    {
        mode = RepeaterToneMode.None;
        string key = Compact(text);
        if (string.IsNullOrEmpty(key) || key is "none" or "off" or "carrier")
        {
            mode = RepeaterToneMode.None;
            return true;
        }

        if (key is "ctcss" or "pl" or "tone" or "t")
        {
            mode = RepeaterToneMode.Ctcss;
            return true;
        }

        if (key is "dcs" or "dtcs" or "dpl")
        {
            mode = RepeaterToneMode.Dcs;
            return true;
        }

        return false;
    }

    static (RepeaterToneMode Mode, double? CtcssHz, string? Dcs) ParseTone(
        string role,
        string? modeText,
        double? ctcssHz,
        string? dcs,
        bool requiredWhenPresent)
    {
        if (!TryParseToneMode(modeText, out RepeaterToneMode mode))
            throw new ArgumentException($"{role} tone mode has to be none, ctcss, or dcs.");

        if (mode == RepeaterToneMode.None)
            return (mode, null, null);

        if (mode == RepeaterToneMode.Ctcss)
        {
            if (ctcssHz is null || !double.IsFinite(ctcssHz.Value))
                throw new ArgumentException($"{role} CTCSS needs a tone in hertz, such as 100.0.");
            double nearest = NearestCtcss(ctcssHz.Value);
            if (Math.Abs(nearest - ctcssHz.Value) > 0.2)
                throw new ArgumentException($"{role} CTCSS {ctcssHz.Value} Hz is not a standard tone. Pick one from the catalog.");
            return (mode, nearest, null);
        }

        string code = NormalizeDcs(dcs);
        if (string.IsNullOrEmpty(code))
            throw new ArgumentException($"{role} DCS needs a three-digit code, such as 023.");
        if (!DcsCodes.Contains(code, StringComparer.Ordinal))
            throw new ArgumentException($"{role} DCS {code} is not a standard code. Pick one from the catalog.");
        return (mode, null, code);
    }

    static double NearestCtcss(double hz) =>
        CtcssHz.MinBy(tone => Math.Abs(tone - hz));

    static string NormalizeDcs(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";
        string digits = DcsDigits().Replace(text.Trim().ToUpperInvariant(), "");
        if (digits.Length == 0)
            return "";
        if (digits.Length > 3)
            digits = digits[^3..];
        return digits.PadLeft(3, '0');
    }

    static string? CleanOptional(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        string body = text.Trim();
        if (body.Length > max)
            throw new ArgumentException($"Keep optional text to {max} characters.");
        return body;
    }

    static string? JoinCaution(params string?[] parts)
    {
        string[] kept = parts.Where(part => !string.IsNullOrWhiteSpace(part)).Select(part => part!.Trim()).ToArray();
        return kept.Length == 0 ? null : string.Join(" ", kept);
    }

    static string Compact(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "" : text.Trim().ToLowerInvariant().Replace(" ", "");

    static IEnumerable<(double From, double To, double Offset, string Name)> StandardOffsets()
    {
        yield return (29_000_000, 29_700_000, 100_000, "10 m");
        yield return (51_000_000, 54_000_000, 1_000_000, "6 m");
        yield return (144_000_000, 148_000_000, 600_000, "2 m");
        yield return (222_000_000, 225_000_000, 1_600_000, "1.25 m");
        yield return (420_000_000, 450_000_000, 5_000_000, "70 cm");
        yield return (902_000_000, 928_000_000, 12_000_000, "33 cm");
        yield return (1_240_000_000, 1_300_000_000, 12_000_000, "23 cm");
    }

    [GeneratedRegex(@"[^0-9]")]
    private static partial Regex DcsDigits();
}

/// <summary>One saved repeater or simplex FM phone setting in the station book.</summary>
public sealed record SavedRepeater(
    string Id,
    string Label,
    RepeaterAccess Access,
    double? PowerWatts,
    DateTimeOffset UpdatedAt);

/// <summary>List of saved repeaters plus which one is selected on the desk.</summary>
public sealed record RepeaterBook(string? SelectedId, IReadOnlyList<SavedRepeater> Items);

/// <summary>Page or agent payload when saving a repeater into the book.</summary>
public sealed record SavedRepeaterBody(RepeaterBody? Repeater, string? Label, double? PowerWatts);

/// <summary>
/// Remembers multiple repeater phone settings. The operator can pick one, save edits, add another, or delete.
/// An older single-settings file is promoted into a one-entry book on first load.
/// </summary>
public sealed class RepeaterMemory
{
    readonly object gate = new();
    readonly JsonSerializerOptions json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    readonly string path;

    public RepeaterMemory(string path) => this.path = path;

    public RepeaterBook Book()
    {
        lock (gate)
            return Snapshot(ReadUnlocked());
    }

    public RepeaterAccess? Selected()
    {
        lock (gate)
        {
            BookFile book = ReadUnlocked();
            return Find(book, book.SelectedId)?.Access;
        }
    }

    public RepeaterBook Select(string? id)
    {
        lock (gate)
        {
            BookFile book = ReadUnlocked();
            if (string.IsNullOrWhiteSpace(id))
            {
                book.SelectedId = null;
            }
            else
            {
                SavedRepeaterFile? hit = Find(book, id);
                if (hit is null)
                    throw new ArgumentException("That saved repeater is not in the book.");
                book.SelectedId = hit.Id;
            }

            WriteUnlocked(book);
            return Snapshot(book);
        }
    }

    public RepeaterBook Create(RepeaterAccess access, string? label, double? powerWatts)
    {
        lock (gate)
        {
            BookFile book = ReadUnlocked();
            var entry = new SavedRepeaterFile
            {
                Id = Guid.NewGuid().ToString("N")[..8],
                Label = NormalizeLabel(label, access),
                Access = access,
                PowerWatts = NormalizePower(powerWatts),
                UpdatedAt = DateTimeOffset.Now
            };
            book.Items.Add(entry);
            book.SelectedId = entry.Id;
            WriteUnlocked(book);
            return Snapshot(book);
        }
    }

    public RepeaterBook Update(string id, RepeaterAccess access, string? label, double? powerWatts)
    {
        lock (gate)
        {
            BookFile book = ReadUnlocked();
            SavedRepeaterFile entry = Find(book, id)
                ?? throw new ArgumentException("That saved repeater is not in the book.");
            entry.Label = NormalizeLabel(label, access);
            entry.Access = access;
            entry.PowerWatts = NormalizePower(powerWatts);
            entry.UpdatedAt = DateTimeOffset.Now;
            book.SelectedId = entry.Id;
            WriteUnlocked(book);
            return Snapshot(book);
        }
    }

    public RepeaterBook Delete(string id)
    {
        lock (gate)
        {
            BookFile book = ReadUnlocked();
            int removed = book.Items.RemoveAll(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (removed == 0)
                throw new ArgumentException("That saved repeater is not in the book.");
            if (book.SelectedId is not null && book.SelectedId.Equals(id, StringComparison.OrdinalIgnoreCase))
                book.SelectedId = book.Items.Count > 0 ? book.Items[^1].Id : null;
            WriteUnlocked(book);
            return Snapshot(book);
        }
    }

    /// <summary>Legacy helper: keep the selected entry in sync when a draft is held, or add one if the book is empty.</summary>
    public void RememberUse(RepeaterAccess access, double? powerWatts = null)
    {
        lock (gate)
        {
            BookFile book = ReadUnlocked();
            SavedRepeaterFile? selected = Find(book, book.SelectedId);
            if (selected is not null)
            {
                selected.Access = access;
                selected.Label = NormalizeLabel(selected.Label, access);
                if (powerWatts is { } watts)
                    selected.PowerWatts = NormalizePower(watts);
                selected.UpdatedAt = DateTimeOffset.Now;
            }
            else if (book.Items.Count == 0)
            {
                var entry = new SavedRepeaterFile
                {
                    Id = Guid.NewGuid().ToString("N")[..8],
                    Label = NormalizeLabel(null, access),
                    Access = access,
                    PowerWatts = NormalizePower(powerWatts),
                    UpdatedAt = DateTimeOffset.Now
                };
                book.Items.Add(entry);
                book.SelectedId = entry.Id;
            }
            else
            {
                return;
            }

            WriteUnlocked(book);
        }
    }

    BookFile ReadUnlocked()
    {
        try
        {
            if (!File.Exists(path))
                return new BookFile();

            string text = File.ReadAllText(path);
            using JsonDocument doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("items", out _))
            {
                BookFile? book = JsonSerializer.Deserialize<BookFile>(text, json) ?? new BookFile();
                book.Items ??= [];
                Promote(book);
                return book;
            }

            RepeaterAccess? legacy = JsonSerializer.Deserialize<RepeaterAccess>(text, json);
            if (legacy is null)
                return new BookFile();

            RepeaterAccess access = StationRepeater.Parse(ToBody(legacy));
            var entry = new SavedRepeaterFile
            {
                Id = Guid.NewGuid().ToString("N")[..8],
                Label = NormalizeLabel(null, access),
                Access = access,
                UpdatedAt = DateTimeOffset.Now
            };
            var migrated = new BookFile { SelectedId = entry.Id, Items = [entry] };
            WriteUnlocked(migrated);
            return migrated;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return new BookFile();
        }
    }

    void WriteUnlocked(BookFile book)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(path, JsonSerializer.Serialize(book, json));
    }

    static void Promote(BookFile book)
    {
        for (int i = 0; i < book.Items.Count; i++)
        {
            SavedRepeaterFile item = book.Items[i];
            if (string.IsNullOrWhiteSpace(item.Id))
                item.Id = Guid.NewGuid().ToString("N")[..8];
            try
            {
                item.Access = StationRepeater.Parse(ToBody(item.Access));
            }
            catch (ArgumentException)
            {
                book.Items.RemoveAt(i);
                i--;
                continue;
            }

            item.Label = NormalizeLabel(item.Label, item.Access);
            item.PowerWatts = NormalizePower(item.PowerWatts);
        }

        if (book.SelectedId is not null && Find(book, book.SelectedId) is null)
            book.SelectedId = book.Items.Count > 0 ? book.Items[0].Id : null;
    }

    static SavedRepeaterFile? Find(BookFile book, string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;
        return book.Items.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    static RepeaterBook Snapshot(BookFile book)
    {
        var items = book.Items
            .OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
            .Select(item => new SavedRepeater(
                item.Id,
                item.Label,
                item.Access,
                item.PowerWatts,
                item.UpdatedAt))
            .ToList();
        return new RepeaterBook(book.SelectedId, items);
    }

    internal static string NormalizeLabel(string? label, RepeaterAccess access)
    {
        if (!string.IsNullOrWhiteSpace(label))
            return label.Trim();
        if (!string.IsNullOrWhiteSpace(access.Name))
            return access.Name.Trim();
        if (!string.IsNullOrWhiteSpace(access.RepeaterCallSign))
            return access.RepeaterCallSign.Trim();
        string mhz = (access.OutputFrequencyHz / 1e6).ToString("0.####", CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(access.City))
            return $"{mhz} · {access.City.Trim()}";
        return mhz + " MHz";
    }

    static double? NormalizePower(double? powerWatts)
    {
        if (powerWatts is null || !double.IsFinite(powerWatts.Value) || powerWatts.Value <= 0)
            return null;
        return Math.Min(1_500, powerWatts.Value);
    }

    static RepeaterBody ToBody(RepeaterAccess access) => new(
        access.OutputFrequencyHz,
        access.InputFrequencyHz,
        access.Shift.ToString(),
        access.OffsetHz,
        access.EncodeMode.ToString(),
        access.EncodeCtcssHz,
        access.EncodeDcs,
        access.DecodeMode.ToString(),
        access.DecodeCtcssHz,
        access.DecodeDcs,
        access.Narrow,
        access.RepeaterCallSign,
        access.Name,
        access.City,
        access.Notes);

    sealed class BookFile
    {
        public string? SelectedId { get; set; }
        public List<SavedRepeaterFile> Items { get; set; } = [];
    }

    sealed class SavedRepeaterFile
    {
        public string Id { get; set; } = "";
        public string Label { get; set; } = "";
        public RepeaterAccess Access { get; set; } = null!;
        public double? PowerWatts { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }
}
