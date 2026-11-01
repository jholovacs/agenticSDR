using AgenticSdr.Dsp;

namespace AgenticSdr.Host;

/// <summary>
/// A published spot where operators listen for each other, such as 146.520 MHz or an FT8 dial.
/// ToleranceHz is how far a signal can sit from that spot and still receive the name.
/// </summary>
public sealed record CallingChannel(string Name, double FrequencyHz, RadioMode Mode, double ToleranceHz);

/// <summary>A slice of a band with one usual mode, such as "2 m CW" or "FM broadcast".</summary>
public sealed record BandSegment(string Name, double StartHz, double EndHz, RadioMode PreferredMode);

/// <summary>
/// One named range: an amateur band, a broadcast service, NOAA weather, CB, GMRS, or DMR.
/// The meter names (2 m, 40 m) are the approximate wavelength, not the width of the range.
/// CB, GMRS, and DMR are receive-only services. DMR spans are the pieces that are scanned, not the whole start-to-end gap.
/// </summary>
public sealed record HamBand(
    string Id,
    string Name,
    double StartHz,
    double EndHz,
    BandSegment[] Segments,
    CallingChannel[] Calling);

/// <summary>
/// The United States (ITU region 2) amateur plan from 160 m through 23 cm, plus AM broadcast, FM broadcast, NOAA weather, CB, GMRS, and DMR.
/// Source labels and mode hints come from here. It does not assign station names.
/// A band is offered on the page when the open radio's tuning range reaches it.
/// </summary>
public static class Region2BandPlan
{
    public static IReadOnlyList<HamBand> Bands { get; } =
    [
        Band("160m", "160 m", 1_800_000, 2_000_000,
            [Seg("160 m CW", 1_800_000, 1_840_000, RadioMode.Cw), Seg("160 m LSB", 1_840_000, 2_000_000, RadioMode.Lsb)],
            [Call("160 m FT8", 1_840_000, RadioMode.Ft8, 3_600)]),
        Band("80m", "80 m", 3_500_000, 4_000_000,
            [Seg("80 m CW", 3_500_000, 3_600_000, RadioMode.Cw), Seg("80 m LSB", 3_600_000, 4_000_000, RadioMode.Lsb)],
            [Call("80 m FT8", 3_573_000, RadioMode.Ft8, 3_600)]),
        Band("60m", "60 m", 5_330_500, 5_406_500,
            [Seg("60 m USB channelized", 5_330_500, 5_406_500, RadioMode.Usb)],
            [Call("60 m FT8", 5_357_000, RadioMode.Ft8, 3_600)]),
        Band("40m", "40 m", 7_000_000, 7_300_000,
            [Seg("40 m CW", 7_000_000, 7_125_000, RadioMode.Cw), Seg("40 m LSB", 7_125_000, 7_300_000, RadioMode.Lsb)],
            [Call("40 m FT8", 7_074_000, RadioMode.Ft8, 3_600)]),
        Band("30m", "30 m", 10_100_000, 10_150_000,
            [Seg("30 m CW", 10_100_000, 10_150_000, RadioMode.Cw)],
            [Call("30 m FT8", 10_136_000, RadioMode.Ft8, 3_600)]),
        Band("20m", "20 m", 14_000_000, 14_350_000,
            [Seg("20 m CW", 14_000_000, 14_150_000, RadioMode.Cw), Seg("20 m USB", 14_150_000, 14_350_000, RadioMode.Usb)],
            [
                Call("20 m FT8", 14_074_000, RadioMode.Ft8, 3_600),
                Call("20 m USB net", 14_300_000, RadioMode.Usb, 5_000)
            ]),
        Band("17m", "17 m", 18_068_000, 18_168_000,
            [Seg("17 m CW", 18_068_000, 18_095_000, RadioMode.Cw), Seg("17 m USB", 18_095_000, 18_168_000, RadioMode.Usb)],
            [Call("17 m FT8", 18_100_000, RadioMode.Ft8, 3_600)]),
        Band("15m", "15 m", 21_000_000, 21_450_000,
            [Seg("15 m CW", 21_000_000, 21_200_000, RadioMode.Cw), Seg("15 m USB", 21_200_000, 21_450_000, RadioMode.Usb)],
            [Call("15 m FT8", 21_074_000, RadioMode.Ft8, 3_600)]),
        Band("12m", "12 m", 24_890_000, 24_990_000,
            [Seg("12 m CW", 24_890_000, 24_930_000, RadioMode.Cw), Seg("12 m USB", 24_930_000, 24_990_000, RadioMode.Usb)],
            [Call("12 m FT8", 24_915_000, RadioMode.Ft8, 3_600)]),
        Band("10m", "10 m", 28_000_000, 29_700_000,
            [
                Seg("10 m CW", 28_000_000, 28_300_000, RadioMode.Cw),
                Seg("10 m USB", 28_300_000, 29_000_000, RadioMode.Usb),
                Seg("10 m FM", 29_000_000, 29_700_000, RadioMode.Nbfm)
            ],
            [
                Call("10 m FT8", 28_074_000, RadioMode.Ft8, 3_600),
                Call("10 m USB calling", 28_400_000, RadioMode.Usb, 3_000),
                Call("10 m FM calling", 29_600_000, RadioMode.Nbfm, 7_500)
            ]),
        Band("6m", "6 m", 50_000_000, 54_000_000,
            [
                Seg("6 m CW", 50_000_000, 50_100_000, RadioMode.Cw),
                Seg("6 m USB", 50_100_000, 50_600_000, RadioMode.Usb),
                Seg("6 m FM", 51_000_000, 54_000_000, RadioMode.Nbfm)
            ],
            [
                Call("6 m FT8", 50_313_000, RadioMode.Ft8, 3_600),
                Call("6 m FT8", 50_323_000, RadioMode.Ft8, 3_600),
                Call("6 m SSB calling", 50_125_000, RadioMode.Usb, 3_000),
                Call("6 m FM calling", 52_525_000, RadioMode.Nbfm, 7_500)
            ]),
        Band("2m", "2 m", 144_000_000, 148_000_000,
            [
                Seg("2 m CW", 144_000_000, 144_100_000, RadioMode.Cw),
                Seg("2 m SSB", 144_100_000, 144_275_000, RadioMode.Usb),
                Seg("2 m mixed", 144_275_000, 144_500_000, RadioMode.Unknown),
                Seg("2 m FM repeater", 144_500_000, 145_500_000, RadioMode.Nbfm),
                Seg("2 m FM", 145_500_000, 146_400_000, RadioMode.Nbfm),
                Seg("2 m FM simplex", 146_400_000, 146_580_000, RadioMode.Nbfm),
                Seg("2 m FM", 146_580_000, 146_610_000, RadioMode.Nbfm),
                Seg("2 m FM repeater output", 146_610_000, 147_390_000, RadioMode.Nbfm),
                Seg("2 m FM", 147_390_000, 147_420_000, RadioMode.Nbfm),
                Seg("2 m FM simplex", 147_420_000, 147_570_000, RadioMode.Nbfm),
                Seg("2 m FM repeater input", 147_600_000, 147_990_000, RadioMode.Nbfm)
            ],
            [
                Call("2 m FT8", 144_174_000, RadioMode.Ft8, 3_600),
                Call("2 m SSB calling", 144_200_000, RadioMode.Usb, 3_000),
                Call("2 m national FM simplex calling", 146_520_000, RadioMode.Nbfm, 7_500)
            ]),
        Band("1.25m", "1.25 m", 222_000_000, 225_000_000,
            [
                Seg("1.25 m CW", 222_000_000, 222_150_000, RadioMode.Cw),
                Seg("1.25 m FM", 222_150_000, 225_000_000, RadioMode.Nbfm)
            ],
            [
                Call("1.25 m FT8", 222_065_000, RadioMode.Ft8, 3_600),
                Call("1.25 m FM calling", 223_500_000, RadioMode.Nbfm, 7_500)
            ]),
        Band("70cm", "70 cm", 420_000_000, 450_000_000,
            [
                Seg("70 cm CW", 432_000_000, 432_100_000, RadioMode.Cw),
                Seg("70 cm SSB", 432_100_000, 432_300_000, RadioMode.Usb),
                Seg("70 cm FM", 440_000_000, 450_000_000, RadioMode.Nbfm)
            ],
            [
                Call("70 cm FT8", 432_174_000, RadioMode.Ft8, 3_600),
                Call("70 cm weak-signal calling", 432_100_000, RadioMode.Usb, 3_000),
                Call("70 cm FM simplex calling", 446_000_000, RadioMode.Nbfm, 7_500)
            ]),
        Band("33cm", "33 cm", 902_000_000, 928_000_000,
            [Seg("33 cm FM", 902_000_000, 928_000_000, RadioMode.Nbfm)]),
        Band("23cm", "23 cm", 1_240_000_000, 1_300_000_000,
            [
                Seg("23 cm weak signal", 1_296_000_000, 1_296_200_000, RadioMode.Usb),
                Seg("23 cm FM", 1_240_000_000, 1_300_000_000, RadioMode.Nbfm)
            ],
            [
                Call("23 cm FT8", 1_296_174_000, RadioMode.Ft8, 3_600),
                Call("23 cm weak-signal calling", 1_296_100_000, RadioMode.Usb, 3_000)
            ]),
        Band("am-bc", "AM broadcast", 530_000, 1_700_000,
            [Seg("AM broadcast", 530_000, 1_700_000, RadioMode.Am)]),
        Band("fm-bc", "FM broadcast", 88_000_000, 108_000_000,
            [Seg("FM broadcast", 88_000_000, 108_000_000, RadioMode.Wbfm)]),
        Band("noaa", "NOAA weather", 162_400_000, 162_550_000,
            [Seg("NOAA weather radio", 162_400_000, 162_550_000, RadioMode.Nbfm)],
            [
                Call("NOAA WX2", 162_400_000, RadioMode.Nbfm, 8_000),
                Call("NOAA WX4", 162_425_000, RadioMode.Nbfm, 8_000),
                Call("NOAA WX5", 162_450_000, RadioMode.Nbfm, 8_000),
                Call("NOAA WX3", 162_475_000, RadioMode.Nbfm, 8_000),
                Call("NOAA WX6", 162_500_000, RadioMode.Nbfm, 8_000),
                Call("NOAA WX7", 162_525_000, RadioMode.Nbfm, 8_000),
                Call("NOAA WX1", 162_550_000, RadioMode.Nbfm, 8_000)
            ]),
        Band("cb", "CB", 26_965_000, 27_405_000,
            [Seg("CB", 26_965_000, 27_405_000, RadioMode.Am)],
            CbChannels()),
        Band("gmrs", "GMRS", 462_550_000, 467_725_000,
            [
                Seg("GMRS 462 MHz", 462_550_000, 462_725_000, RadioMode.Nbfm),
                Seg("GMRS 467 MHz", 467_550_000, 467_725_000, RadioMode.Nbfm)
            ],
            GmrsChannels()),
        Band("dmr", "DMR", 145_200_000, 450_000_000,
            [
                Seg("VHF DMR", 145_200_000, 145_500_000, RadioMode.Dmr),
                Seg("UHF DMR", 440_000_000, 450_000_000, RadioMode.Dmr),
                Seg("VHF M17", 144_000_000, 148_000_000, RadioMode.M17),
                Seg("UHF M17", 420_000_000, 450_000_000, RadioMode.M17)
            ])
    ];

    /// <summary>The pieces of a band that are scanned and matched to an antenna. GMRS and DMR skip the gaps between their channel blocks.</summary>
    public static IReadOnlyList<(double StartHz, double EndHz)> Coverage(HamBand band)
    {
        if (band.Id is "gmrs" or "dmr")
            return band.Segments.Select(segment => (segment.StartHz, segment.EndHz)).ToList();
        return [(band.StartHz, band.EndHz)];
    }

    public static bool Covers(HamBand band, double frequencyHz, double slackHz = 0) =>
        Coverage(band).Any(span => frequencyHz >= span.StartHz - slackHz && frequencyHz <= span.EndHz + slackHz);

    public static double ScanBandwidthHz(HamBand band) =>
        band.Segments.Any(segment => segment.PreferredMode == RadioMode.Wbfm) ? 300_000 : 20_000;

    public static double MergeDistanceHz(HamBand band) =>
        band.Segments.Any(segment => segment.PreferredMode == RadioMode.Wbfm) ? 150_000 : 20_000;

    public static HamBand? Find(string? bandId)
    {
        if (string.IsNullOrWhiteSpace(bandId))
            return null;
        return Bands.FirstOrDefault(b => b.Id.Equals(bandId.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public static BandHint HintAt(double frequencyHz)
    {
        foreach (var band in Bands)
        {
            if (!Covers(band, frequencyHz))
                continue;

            CallingChannel? calling = null;
            foreach (var channel in band.Calling)
            {
                if (channel.Mode == RadioMode.Ft8)
                {
                    double delta = frequencyHz - channel.FrequencyHz;
                    if (delta >= -500 && delta <= channel.ToleranceHz)
                        calling = channel;
                }
                else if (Math.Abs(frequencyHz - channel.FrequencyHz) <= channel.ToleranceHz)
                {
                    calling = channel;
                }
            }

            BandSegment? segment = null;
            foreach (var item in band.Segments)
            {
                if (frequencyHz >= item.StartHz && frequencyHz <= item.EndHz)
                    segment = item;
            }

            // Later overlapping segments win, except a more specific earlier CW/SSB slice
            // is listed first. The last match is used so 23 cm weak-signal overrides the wide FM segment
            // only if it is checked after. Re-scan for the narrowest segment instead.
            segment = Narrowest(band, frequencyHz) ?? segment;
            RadioMode mode = calling?.Mode ?? segment?.PreferredMode ?? RadioMode.Unknown;
            return new BandHint(mode, segment?.Name, calling?.Name);
        }

        return new BandHint(RadioMode.Unknown);
    }

    public static string SourceLabel(double frequencyHz)
    {
        BandHint hint = HintAt(frequencyHz);
        if (!string.IsNullOrWhiteSpace(hint.CallingName))
            return hint.CallingName;
        if (!string.IsNullOrWhiteSpace(hint.SegmentName))
            return hint.SegmentName;
        foreach (var band in Bands)
        {
            if (Covers(band, frequencyHz))
                return band.Name;
        }

        return "Outside the region 2 amateur plan";
    }

    public static bool TryFt8Dial(double frequencyHz, out double dialHz)
    {
        dialHz = 0;
        double best = double.MaxValue;
        foreach (HamBand band in Bands)
        {
            foreach (CallingChannel channel in band.Calling)
            {
                if (channel.Mode != RadioMode.Ft8)
                    continue;
                double delta = frequencyHz - channel.FrequencyHz;
                if (delta < -500 || delta > channel.ToleranceHz)
                    continue;
                double distance = Math.Abs(delta);
                if (distance >= best)
                    continue;
                best = distance;
                dialHz = channel.FrequencyHz;
            }
        }

        return best < double.MaxValue;
    }

    private static BandSegment? Narrowest(HamBand band, double frequencyHz)
    {
        BandSegment? best = null;
        double width = double.MaxValue;
        foreach (var item in band.Segments)
        {
            if (frequencyHz < item.StartHz || frequencyHz > item.EndHz)
                continue;
            double span = item.EndHz - item.StartHz;
            if (span < width)
            {
                width = span;
                best = item;
            }
        }

        return best;
    }

    private static HamBand Band(
        string id,
        string name,
        double start,
        double end,
        BandSegment[] segments,
        CallingChannel[]? calling = null) =>
        new(id, name, start, end, segments, calling ?? []);

    private static BandSegment Seg(string name, double start, double end, RadioMode mode) =>
        new(name, start, end, mode);

    private static CallingChannel Call(string name, double frequencyHz, RadioMode mode, double toleranceHz) =>
        new(name, frequencyHz, mode, toleranceHz);

    private static CallingChannel[] CbChannels()
    {
        double[] mhz =
        [
            26.965, 26.975, 26.985, 27.005, 27.015, 27.025, 27.035, 27.055, 27.065, 27.075,
            27.085, 27.105, 27.115, 27.125, 27.135, 27.155, 27.165, 27.175, 27.185, 27.205,
            27.215, 27.225, 27.255, 27.235, 27.245, 27.265, 27.275, 27.285, 27.295, 27.305,
            27.315, 27.325, 27.335, 27.345, 27.355, 27.365, 27.375, 27.385, 27.395, 27.405
        ];
        var channels = new CallingChannel[mhz.Length];
        for (int i = 0; i < mhz.Length; i++)
        {
            int number = i + 1;
            string name = number switch
            {
                9 => "CB 9 emergency",
                19 => "CB 19 highway",
                _ => $"CB {number}"
            };
            channels[i] = Call(name, mhz[i] * 1_000_000, RadioMode.Am, 4_000);
        }

        return channels;
    }

    private static CallingChannel[] GmrsChannels()
    {
        var channels = new List<CallingChannel>();
        for (int i = 0; i < 7; i++)
            channels.Add(Call($"GMRS {i + 1}", 462_562_500 + i * 25_000, RadioMode.Nbfm, 5_000));
        for (int i = 0; i < 7; i++)
            channels.Add(Call($"GMRS {i + 8}", 467_562_500 + i * 25_000, RadioMode.Nbfm, 5_000));
        for (int i = 0; i < 8; i++)
        {
            double output = 462_550_000 + i * 25_000;
            int number = i + 15;
            channels.Add(Call($"GMRS {number}", output, RadioMode.Nbfm, 5_000));
            channels.Add(Call($"GMRS {number} input", output + 5_000_000, RadioMode.Nbfm, 5_000));
        }

        return channels.ToArray();
    }
}
