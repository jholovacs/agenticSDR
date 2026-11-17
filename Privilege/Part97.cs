namespace AgenticSdr.Privilege;

/// <summary>
/// United States amateur transmitting privileges for a station in ITU Region 2.
/// Frequency edges and emission limits follow 47 CFR 97.301, 97.303, 97.305, 97.307, and 97.313.
/// CW is allowed on every frequency the license may occupy. Phone and data stay in their segments.
/// The whole emitted signal has to fit: USB sits above the dial, LSB sits below it, CW is a narrow signal on the dial.
/// This is not an FCC license lookup, and it does not know geographic limits that depend on where the antenna is.
/// </summary>
public static class Part97
{
    const double CwHalfHz = 100;
    const double SidebandHz = 2_800;
    const double AmHalfHz = 3_000;
    const double FmHalfHz = 8_000;

    const string SeventyCaution =
        "Geographic limits still apply on 70 cm, including no 420–430 MHz north of Line A (§97.303). This check does not know where the antenna is.";

    const string ThirtyThreeCaution =
        "Parts of 33 cm are closed in portions of Texas, New Mexico, Colorado, and Wyoming (§97.303). This check does not know where the antenna is.";

    const string FourMmCaution =
        "The cap on 76–81 GHz is 316 W EIRP, not 316 W PEP.";

    public static PrivilegeDecision Evaluate(LicenseClass license, AmateurMode mode, double frequencyHz, double powerWatts)
    {
        if (!double.IsFinite(frequencyHz) || frequencyHz <= 0)
            return Deny("Frequency has to be a positive number of hertz.");
        if (!double.IsFinite(powerWatts) || powerWatts <= 0)
            return Deny("Power has to be greater than zero.");
        if (powerWatts > 1_500)
            return Deny("Part 97 caps transmitter power at 1500 W PEP.");

        if (TryForbiddenService(frequencyHz, out PrivilegeDecision forbidden))
            return forbidden;

        if (frequencyHz is >= 5_250_000 and <= 5_450_000)
            return EvaluateSixtyMeters(license, mode, frequencyHz, powerWatts);

        if (frequencyHz is >= 219_000_000 and < 220_000_000)
            return Deny("219–220 MHz is only for fixed digital message-forwarding systems that have already given the required notice. This check will not authorize it.");

        if (frequencyHz is >= 135_700 and <= 137_800 || frequencyHz is >= 472_000 and <= 479_000)
            return Deny("2200 m and 630 m need a fixed station and a Utilities Telecom Council notice before the first transmission. This check will not authorize them.");

        (double low, double high) = Occupancy(mode, frequencyHz);
        Grant[] grants = GrantsFor(license);
        var containing = new List<Grant>();
        foreach (Grant grant in grants)
        {
            if (low >= grant.StartHz && high <= grant.EndHz)
                containing.Add(grant);
        }

        EmissionKind kind = KindOf(mode);
        if (containing.Count > 0)
            return DecideInside(license, mode, kind, powerWatts, containing);

        foreach (Grant grant in grants)
        {
            if (frequencyHz >= grant.StartHz && frequencyHz <= grant.EndHz)
                return Deny($"The {AmateurWords.ModeName(mode)} signal would extend outside the frequencies a {AmateurWords.LicenseName(license)} licensee may use on {grant.Band}.");
        }

        return Deny($"A {AmateurWords.LicenseName(license)} licensee has no privileges at {FormatHz(frequencyHz)}.");
    }

    public static string Summary(LicenseClass license) => license switch
    {
        LicenseClass.Technician =>
            "Technician: CW on parts of 80, 40, and 15 m; CW, data, and USB phone on part of 10 m; all HF at 200 W PEP. On 6 m and above, all amateur privileges except CW only in the bottom 100 kHz of 6 m and 2 m. No 160, 60, 30, 20, 17, or 12 m.",
        LicenseClass.General =>
            "General: most HF bands, excluding Extra-only edges. 30 m is CW and data at 200 W PEP. 60 m is channelized. All amateur privileges on 6 m and above.",
        LicenseClass.Extra =>
            "Amateur Extra: every US amateur HF allocation, including the Extra-only segments. 30 m is still 200 W PEP. 60 m is still channelized. All amateur privileges on 6 m and above.",
        _ => "Unknown license class."
    };

    public static (double LowHz, double HighHz) Occupancy(AmateurMode mode, double frequencyHz)
    {
        return mode switch
        {
            AmateurMode.Cw => (frequencyHz - CwHalfHz, frequencyHz + CwHalfHz),
            AmateurMode.Usb => (frequencyHz, frequencyHz + SidebandHz),
            AmateurMode.Lsb => (frequencyHz - SidebandHz, frequencyHz),
            AmateurMode.Am => (frequencyHz - AmHalfHz, frequencyHz + AmHalfHz),
            AmateurMode.Nbfm => (frequencyHz - FmHalfHz, frequencyHz + FmHalfHz),
            AmateurMode.Packet when frequencyHz >= 30_000_000 => (frequencyHz - FmHalfHz, frequencyHz + FmHalfHz),
            AmateurMode.Ribbit when frequencyHz >= 30_000_000 => (frequencyHz - FmHalfHz, frequencyHz + FmHalfHz),
            _ => (frequencyHz, frequencyHz + SidebandHz)
        };
    }

    static PrivilegeDecision DecideInside(
        LicenseClass license,
        AmateurMode mode,
        EmissionKind kind,
        double powerWatts,
        List<Grant> containing)
    {
        var kindOk = new List<Grant>();
        foreach (Grant grant in containing)
        {
            if (grant.Emissions.HasFlag(kind))
                kindOk.Add(grant);
        }

        if (kindOk.Count == 0)
        {
            Grant sample = containing[0];
            return Deny(
                $"This part of {sample.Band} is reserved for {Describe(sample.Emissions)}. " +
                $"{AmateurWords.ModeName(mode)} is not authorized there for a {AmateurWords.LicenseName(license)} licensee.");
        }

        var shapeOk = new List<Grant>();
        foreach (Grant grant in kindOk)
        {
            if (ShapeAllows(mode, grant.Phone))
                shapeOk.Add(grant);
        }

        if (shapeOk.Count == 0)
        {
            if (mode == AmateurMode.Nbfm)
                return Deny("Narrow FM phone is authorized on 10 m above 29 MHz and on VHF and UHF, and only on a segment your license includes.");
            return Deny($"A {AmateurWords.LicenseName(license)} licensee may use only CW and USB phone (J3E or R3E) on this part of 10 m.");
        }

        Grant chosen = shapeOk[0];
        foreach (Grant grant in shapeOk)
        {
            if (grant.MaxWatts < chosen.MaxWatts)
                chosen = grant;
        }

        if (powerWatts > chosen.MaxWatts)
        {
            string unit = chosen.Caution?.Contains("EIRP", StringComparison.Ordinal) == true ? "EIRP" : "PEP";
            return Deny($"Part 97 caps this part of {chosen.Band} at {FormatWatts(chosen.MaxWatts)} {unit} for a {AmateurWords.LicenseName(license)} licensee.");
        }

        string reason =
            $"{AmateurWords.LicenseName(license)} may transmit {AmateurWords.ModeName(mode)} here on {chosen.Band}, up to {FormatWatts(chosen.MaxWatts)} {(chosen.Caution?.Contains("EIRP", StringComparison.Ordinal) == true ? "EIRP" : "PEP")}.";
        return Allow(reason, chosen.Band, chosen.MaxWatts, chosen.Caution);
    }

    static PrivilegeDecision EvaluateSixtyMeters(LicenseClass license, AmateurMode mode, double frequencyHz, double powerWatts)
    {
        if (license is LicenseClass.Technician)
            return Deny("Technician licensees have no 60 m privileges.");

        if (mode is AmateurMode.Lsb or AmateurMode.Am or AmateurMode.Nbfm)
            return Deny("60 m phone and data are USB. CW sits on a channel center. LSB, AM, and FM are not used on 60 m.");

        foreach ((double center, double carrier) in SixtyChannels)
        {
            if (mode == AmateurMode.Cw && Math.Abs(frequencyHz - center) <= 50)
                return SixtyPower(powerWatts, 100, "60 m channel");
            if (mode != AmateurMode.Cw && Math.Abs(frequencyHz - carrier) <= 50)
                return SixtyPower(powerWatts, 100, "60 m channel");
        }

        (double low, double high) = Occupancy(mode, frequencyHz);
        if (low >= 5_351_500 && high <= 5_366_500)
            return SixtyPower(powerWatts, 9.15, "60 m WRC segment");

        return Deny("60 m is channelized. USB and data use dial 5330.5, 5346.5, 5371.5, or 5403.5 kHz (100 W ERP), or stay inside 5351.5–5366.5 kHz (9.15 W ERP). CW uses center 5332, 5348, 5373, or 5405 kHz.");
    }

    static PrivilegeDecision SixtyPower(double powerWatts, double cap, string band)
    {
        if (powerWatts > cap)
            return Deny($"60 m on {band} is limited to {FormatWatts(cap)} ERP. The watts you enter are treated as ERP.");

        return Allow(
            $"General and above may transmit here on {band}, up to {FormatWatts(cap)} ERP. Enter ERP, not bare transmitter PEP, if the antenna has gain.",
            band,
            cap,
            "60 m power is ERP. Count antenna gain. This check treats the watts you enter as ERP.");
    }

    static bool TryForbiddenService(double frequencyHz, out PrivilegeDecision decision)
    {
        decision = null!;
        string? name = frequencyHz switch
        {
            >= 530_000 and <= 1_700_000 => "AM broadcast",
            >= 26_960_000 and <= 27_410_000 => "CB",
            >= 88_000_000 and <= 108_000_000 => "FM broadcast",
            >= 162_400_000 and <= 162_550_000 => "NOAA weather radio",
            >= 462_000_000 and <= 467_725_000 => "GMRS",
            _ => null
        };
        if (name == null)
            return false;

        decision = Deny($"{name} is not an amateur band. This check will not authorize it.");
        return true;
    }

    static bool ShapeAllows(AmateurMode mode, PhoneShape shape) => mode switch
    {
        AmateurMode.Nbfm => shape == PhoneShape.FmOk,
        AmateurMode.Lsb or AmateurMode.Am => shape != PhoneShape.UsbOnly,
        _ => true
    };

    static EmissionKind KindOf(AmateurMode mode) => mode switch
    {
        AmateurMode.Cw => EmissionKind.Cw,
        AmateurMode.Usb or AmateurMode.Lsb or AmateurMode.Am or AmateurMode.Nbfm => EmissionKind.Phone,
        _ => EmissionKind.Data
    };

    static string Describe(EmissionKind kind)
    {
        var parts = new List<string>();
        if (kind.HasFlag(EmissionKind.Cw))
            parts.Add("CW");
        if (kind.HasFlag(EmissionKind.Phone))
            parts.Add("phone");
        if (kind.HasFlag(EmissionKind.Data))
            parts.Add("data");
        if (parts.Count == 0)
            return "no emission";
        if (parts.Count == 1)
            return parts[0];
        if (parts.Count == 2)
            return parts[0] + " and " + parts[1];
        return parts[0] + ", " + parts[1] + ", and " + parts[2];
    }

    static string FormatHz(double hz)
    {
        if (hz >= 1_000_000_000)
            return $"{hz / 1_000_000_000:0.###} GHz";
        if (hz >= 1_000_000)
            return $"{hz / 1_000_000:0.###} MHz";
        return $"{hz / 1_000:0.###} kHz";
    }

    static string FormatWatts(double watts) => watts >= 10 ? $"{watts:0} W" : $"{watts:0.##} W";

    static PrivilegeDecision Deny(string reason) => new(false, false, reason, null, null, null);

    static PrivilegeDecision Allow(string reason, string band, double maxWatts, string? caution) =>
        new(true, true, reason, band, maxWatts, caution);

    static Grant[] GrantsFor(LicenseClass license) => license switch
    {
        LicenseClass.Technician => TechnicianGrants,
        LicenseClass.General => GeneralGrants,
        LicenseClass.Extra => ExtraGrants,
        _ => []
    };

    static readonly (double Center, double Carrier)[] SixtyChannels =
    [
        (5_332_000, 5_330_500),
        (5_348_000, 5_346_500),
        (5_373_000, 5_371_500),
        (5_405_000, 5_403_500)
    ];

    static readonly Grant[] ExtraGrants = [.. ExtraHf(), .. AboveSixMeters(1_500)];
    static readonly Grant[] GeneralGrants = [.. GeneralHf(), .. AboveSixMeters(1_500)];
    static readonly Grant[] TechnicianGrants = [.. TechnicianHf(), .. AboveSixMeters(1_500)];

    static Grant[] ExtraHf() =>
    [
        new(1_800_000, 2_000_000, EmissionKind.All, 1_500, "160 m"),
        new(3_500_000, 3_600_000, EmissionKind.Cw | EmissionKind.Data, 1_500, "80 m"),
        new(3_600_000, 4_000_000, EmissionKind.Cw | EmissionKind.Phone, 1_500, "75 m"),
        new(7_000_000, 7_125_000, EmissionKind.Cw | EmissionKind.Data, 1_500, "40 m"),
        new(7_125_000, 7_300_000, EmissionKind.Cw | EmissionKind.Phone, 1_500, "40 m"),
        new(10_100_000, 10_150_000, EmissionKind.Cw | EmissionKind.Data, 200, "30 m"),
        new(14_000_000, 14_150_000, EmissionKind.Cw | EmissionKind.Data, 1_500, "20 m"),
        new(14_150_000, 14_350_000, EmissionKind.Cw | EmissionKind.Phone, 1_500, "20 m"),
        new(18_068_000, 18_110_000, EmissionKind.Cw | EmissionKind.Data, 1_500, "17 m"),
        new(18_110_000, 18_168_000, EmissionKind.Cw | EmissionKind.Phone, 1_500, "17 m"),
        new(21_000_000, 21_200_000, EmissionKind.Cw | EmissionKind.Data, 1_500, "15 m"),
        new(21_200_000, 21_450_000, EmissionKind.Cw | EmissionKind.Phone, 1_500, "15 m"),
        new(24_890_000, 24_930_000, EmissionKind.Cw | EmissionKind.Data, 1_500, "12 m"),
        new(24_930_000, 24_990_000, EmissionKind.Cw | EmissionKind.Phone, 1_500, "12 m"),
        new(28_000_000, 28_300_000, EmissionKind.Cw | EmissionKind.Data, 1_500, "10 m"),
        new(28_300_000, 29_000_000, EmissionKind.Cw | EmissionKind.Phone, 1_500, "10 m"),
        new(29_000_000, 29_700_000, EmissionKind.Cw | EmissionKind.Phone, 1_500, "10 m", PhoneShape.FmOk)
    ];

    static Grant[] GeneralHf() =>
    [
        new(1_800_000, 2_000_000, EmissionKind.All, 1_500, "160 m"),
        new(3_525_000, 3_600_000, EmissionKind.Cw | EmissionKind.Data, 1_500, "80 m"),
        new(3_800_000, 4_000_000, EmissionKind.Cw | EmissionKind.Phone, 1_500, "75 m"),
        new(7_025_000, 7_125_000, EmissionKind.Cw | EmissionKind.Data, 1_500, "40 m"),
        new(7_175_000, 7_300_000, EmissionKind.Cw | EmissionKind.Phone, 1_500, "40 m"),
        new(10_100_000, 10_150_000, EmissionKind.Cw | EmissionKind.Data, 200, "30 m"),
        new(14_025_000, 14_150_000, EmissionKind.Cw | EmissionKind.Data, 1_500, "20 m"),
        new(14_225_000, 14_350_000, EmissionKind.Cw | EmissionKind.Phone, 1_500, "20 m"),
        new(18_068_000, 18_110_000, EmissionKind.Cw | EmissionKind.Data, 1_500, "17 m"),
        new(18_110_000, 18_168_000, EmissionKind.Cw | EmissionKind.Phone, 1_500, "17 m"),
        new(21_025_000, 21_200_000, EmissionKind.Cw | EmissionKind.Data, 1_500, "15 m"),
        new(21_275_000, 21_450_000, EmissionKind.Cw | EmissionKind.Phone, 1_500, "15 m"),
        new(24_890_000, 24_930_000, EmissionKind.Cw | EmissionKind.Data, 1_500, "12 m"),
        new(24_930_000, 24_990_000, EmissionKind.Cw | EmissionKind.Phone, 1_500, "12 m"),
        new(28_000_000, 28_300_000, EmissionKind.Cw | EmissionKind.Data, 1_500, "10 m"),
        new(28_300_000, 29_000_000, EmissionKind.Cw | EmissionKind.Phone, 1_500, "10 m"),
        new(29_000_000, 29_700_000, EmissionKind.Cw | EmissionKind.Phone, 1_500, "10 m", PhoneShape.FmOk)
    ];

    static Grant[] TechnicianHf() =>
    [
        new(3_525_000, 3_600_000, EmissionKind.Cw, 200, "80 m"),
        new(7_025_000, 7_125_000, EmissionKind.Cw, 200, "40 m"),
        new(21_025_000, 21_200_000, EmissionKind.Cw, 200, "15 m"),
        new(28_000_000, 28_300_000, EmissionKind.Cw | EmissionKind.Data, 200, "10 m"),
        new(28_300_000, 28_500_000, EmissionKind.Cw | EmissionKind.Phone, 200, "10 m", PhoneShape.UsbOnly)
    ];

    static Grant[] AboveSixMeters(double watts) =>
    [
        new(50_000_000, 50_100_000, EmissionKind.Cw, watts, "6 m"),
        new(50_100_000, 54_000_000, EmissionKind.All, watts, "6 m", PhoneShape.FmOk),
        new(144_000_000, 144_100_000, EmissionKind.Cw, watts, "2 m"),
        new(144_100_000, 148_000_000, EmissionKind.All, watts, "2 m", PhoneShape.FmOk),
        new(222_000_000, 225_000_000, EmissionKind.All, watts, "1.25 m", PhoneShape.FmOk),
        new(420_000_000, 450_000_000, EmissionKind.All, watts, "70 cm", PhoneShape.FmOk, SeventyCaution),
        new(902_000_000, 928_000_000, EmissionKind.All, watts, "33 cm", PhoneShape.FmOk, ThirtyThreeCaution),
        new(1_240_000_000, 1_300_000_000, EmissionKind.All, watts, "23 cm", PhoneShape.FmOk),
        new(2_300_000_000, 2_310_000_000, EmissionKind.All, watts, "13 cm", PhoneShape.FmOk),
        new(2_390_000_000, 2_450_000_000, EmissionKind.All, watts, "13 cm", PhoneShape.FmOk),
        new(5_650_000_000, 5_925_000_000, EmissionKind.All, watts, "5 cm", PhoneShape.FmOk),
        new(10_000_000_000, 10_500_000_000, EmissionKind.All, watts, "3 cm", PhoneShape.FmOk),
        new(24_000_000_000, 24_250_000_000, EmissionKind.All, watts, "1.2 cm", PhoneShape.FmOk),
        new(47_000_000_000, 47_200_000_000, EmissionKind.All, watts, "6 mm", PhoneShape.FmOk),
        new(76_000_000_000, 81_000_000_000, EmissionKind.All, 316, "4 mm", PhoneShape.FmOk, FourMmCaution),
        new(122_250_000_000, 123_000_000_000, EmissionKind.All, watts, "2.5 mm", PhoneShape.FmOk),
        new(134_000_000_000, 141_000_000_000, EmissionKind.All, watts, "2 mm", PhoneShape.FmOk),
        new(241_000_000_000, 250_000_000_000, EmissionKind.All, watts, "1 mm", PhoneShape.FmOk)
    ];

    [Flags]
    enum EmissionKind
    {
        None = 0,
        Cw = 1,
        Data = 2,
        Phone = 4,
        All = Cw | Data | Phone
    }

    enum PhoneShape
    {
        Any,
        UsbOnly,
        FmOk
    }

    readonly record struct Grant(
        double StartHz,
        double EndHz,
        EmissionKind Emissions,
        double MaxWatts,
        string Band,
        PhoneShape Phone = PhoneShape.Any,
        string? Caution = null);
}
