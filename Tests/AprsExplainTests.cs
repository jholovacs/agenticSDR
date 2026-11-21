using AgenticSdr.Dsp;
using AgenticSdr.Processors;

namespace AgenticSdr.Tests;

public class AprsExplainTests
{
    [Theory]
    [InlineData(
        "CABALL>APN382,LTLFLO*,WIDE2:!3258.27NS10713.44W#PHG7830/W4,NMn/W5DIG-8 Digi urfmsi.org",
        "CABALL",
        "digipeater",
        "Kantronics")]
    [InlineData(
        "N7GEE-10*>APDW19*,HELIO*,JACKPK*,LTLFLO*,WIDE2:=3159.35NS11058.94W#PHG5870* Sahuarita Igate/Digi *",
        "N7GEE-10",
        "IGate",
        "Dire Wolf")]
    [InlineData(
        "LTLFLO>APN382:!3211.64N/10736.34W# WIDE2_-N, NM2-N Little Flooritarita, Deming",
        "LTLFLO",
        "digipeater",
        "Deming")]
    [InlineData(
        "ELPASO>APN382,CABALL*,LTLFLO*,WIDE3-1:!!0000008002140000----------------0009004A00000000",
        "ELPASO",
        "Ultimeter",
        "53.2")]
    [InlineData(
        "W7SA-12>AP1WWX*,HELIO*,JACKPK*,WIDE2:@210512z3206.21N/11056.22W_186/000g002t054P000r000h66b10130e1w",
        "W7SA-12",
        "weather",
        "temperature")]
    public void Explains_common_aprs_frames(string raw, string call, string needleA, string needleB)
    {
        string english = AprsExplain.Describe(raw);
        Assert.Contains(call, english, StringComparison.Ordinal);
        Assert.Contains(needleA, english, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(needleB, english, StringComparison.OrdinalIgnoreCase);

        string formatted = AprsExplain.Format(raw);
        Assert.Contains("Raw: " + raw, formatted, StringComparison.Ordinal);
        Assert.Equal(formatted, AprsExplain.Format(formatted));
    }

    [Fact]
    public void Html_entity_gt_is_normalized()
    {
        string english = AprsExplain.Describe("CABALL&gt;APN382:!3258.27NS10713.44W#Digi");
        Assert.Contains("CABALL", english, StringComparison.Ordinal);
        Assert.Contains("digipeater", english, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Phg_decodes_power_and_height()
    {
        string english = AprsExplain.Describe(
            "CABALL>APN382:!3258.27NS10713.44W#PHG7830/W4 Digi");
        Assert.Contains("49 W", english, StringComparison.Ordinal); // 7²
        Assert.Contains("2560 ft", english, StringComparison.Ordinal); // 10 × 2⁸
    }

    [Fact]
    public void Packet_pipeline_includes_plain_english_stage()
    {
        Assert.Equal(["packet", "aprs-explain"], ModePipelines.DefaultIds(RadioMode.Packet));
        Assert.True(ModePipelines.IsKnownId("aprs-explain"));
    }

    [Fact]
    public void Ultimeter_logger_decodes_elpaso_style_hex()
    {
        string english = AprsExplain.Describe(
            "ELPASO>APN382,CABALL*,LTLFLO*,WIDE3-1:!!0000008002140000----------------0009004A00000000");
        Assert.Contains("Ultimeter weather telemetry", english, StringComparison.Ordinal);
        Assert.Contains("53.2 °F", english, StringComparison.Ordinal);
        Assert.Contains("180°", english, StringComparison.Ordinal); // 0x80 → 180°
        Assert.Contains("day 9 of year", english, StringComparison.Ordinal);
        Assert.Contains("01:14", english, StringComparison.Ordinal); // 0x4A minutes
    }

    [Fact]
    public void TryParseWeatherReport_reads_scrambled_rain_field_order()
    {
        const string raw =
            "W7SA-12>AP1WWX*,HELIO*,JACKPK*,WIDE2:@210512z3206.21N/11056.22W_186/000g002t054P000r000h66b10130e1w";
        Assert.True(AprsExplain.TryParseWeatherReport(raw, out AprsWeatherReport report));
        Assert.Equal("W7SA-12", report.Callsign);
        Assert.Equal(186, report.WindDegrees);
        Assert.Equal(0, report.WindMph);
        Assert.Equal(2, report.GustMph);
        Assert.Equal(54, report.TemperatureF);
        Assert.Equal(66, report.HumidityPercent);
        Assert.Equal(0.0, report.RainLastHourIn);
        Assert.Equal(0.0, report.RainSinceMidnightIn);
        Assert.Equal(1013.0, report.PressureMbar);
        Assert.InRange(report.LatitudeDegrees, 32.10, 32.11);
        Assert.InRange(report.LongitudeDegrees, -110.94, -110.93);
    }

    [Fact]
    public void TryParseWeatherReport_rejects_digipeater()
    {
        Assert.False(AprsExplain.TryParseWeatherReport(
            "CABALL>APN382:!3258.27NS10713.44W#PHG7830/W4 Digi",
            out _));
    }

    [Fact]
    public void TryParsePositionFix_reads_digipeater()
    {
        Assert.True(AprsExplain.TryParsePositionFix(
            "N7GEE-10>APDW19:=3159.35NS11058.94W#PHG5870 Digi",
            out AprsPositionFix fix));
        Assert.Equal("N7GEE-10", fix.Callsign);
        Assert.False(fix.IsWeatherSymbol);
        Assert.InRange(fix.LatitudeDegrees, 31.98, 32.00);
    }

    [Fact]
    public void TryParseUltimeterMetrics_reads_elpaso_logger()
    {
        Assert.True(AprsExplain.TryParseUltimeterMetrics(
            "ELPASO>APN382,CABALL*,LTLFLO*,WIDE3-1:!!0000008002140000----------------0009004A00000000",
            out AprsUltimeterMetrics m));
        Assert.Equal("ELPASO", m.Callsign);
        Assert.Equal(53, m.TemperatureF);
        Assert.Equal(180, m.WindDegrees);
    }

    [Fact]
    public void TryParseWeatherReport_accepts_explained_transcript_raw_line()
    {
        string explained = AprsExplain.Format(
            "W7SA-12>AP1WWX:@210512z3206.21N/11056.22W_186/000g002t054r000p000P000h66b10130");
        Assert.True(AprsExplain.TryParseWeatherReport(explained, out AprsWeatherReport report));
        Assert.Equal("W7SA-12", report.Callsign);
        Assert.Equal(54, report.TemperatureF);
    }

    [Fact]
    public void Standard_t_hash_telemetry_lists_channels_and_bits()
    {
        string english = AprsExplain.Describe(
            "N0CALL>APRS:T#123,10,20,30,40,50,01001001");
        Assert.Contains("sequence 123", english, StringComparison.Ordinal);
        Assert.Contains("channel 1 = 10", english, StringComparison.Ordinal);
        Assert.Contains("channel 5 = 50", english, StringComparison.Ordinal);
        Assert.Contains("bit 2", english, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bit 5", english, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bit 8", english, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Telemetry_parm_definition_is_explained()
    {
        string english = AprsExplain.Describe(
            "N0CALL>APRS::N0CALL   :PARM.Battery,Current,Power,Temp,RSSI");
        Assert.Contains("Telemetry setup", english, StringComparison.Ordinal);
        Assert.Contains("parameter names", english, StringComparison.Ordinal);
        Assert.Contains("Battery", english, StringComparison.Ordinal);
    }
}
