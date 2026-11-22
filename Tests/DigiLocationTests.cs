using AgenticSdr.Dsp;

namespace AgenticSdr.Tests;

public sealed class DigiLocationTests
{
    [Theory]
    [InlineData("CQ KJ5SKJ EM13", "ft8", "KJ5SKJ", "EM13")]
    [InlineData("CQ DX W1AW FN31", "ft8", "W1AW", "FN31")]
    [InlineData("K1ABC W9XYZ EN61", "ft8", "W9XYZ", "EN61")]
    [InlineData("-12 dB  1500 Hz  CQ KJ5SKJ EM13", "ft8", "KJ5SKJ", "EM13")]
    // Positive SNR is left-padded to width 4 — same shape the live Format() emits.
    [InlineData("  15 dB  1534 Hz  CQ W9ISP EM69", "ft8", "W9ISP", "EM69")]
    [InlineData("  14 dB  1250 Hz  KF7MT N8FMD EM99", "ft8", "N8FMD", "EM99")]
    [InlineData("12:34:56  -8 dB  1234 Hz  CQ KJ5SKJ EM13", "js8", "KJ5SKJ", "EM13")]
    [InlineData("CQ KJ5SKJ EM13dd", "ft8", "KJ5SKJ", "EM13DD")]
    [InlineData("K1ABC W9XYZ EM13dd", "ft8", "W9XYZ", "EM13DD")]
    public void Parses_ft8_js8_grid_lines(string line, string mode, string call, string grid)
    {
        Assert.True(DigiLocation.TryParse(line, mode, out DigiLocationFix fix));
        Assert.Equal(call, fix.Callsign);
        Assert.Equal(grid, fix.Grid);
        Assert.Equal(mode, fix.Source);
        Assert.True(Maidenhead.TryParse(grid, out double lat, out double lon));
        Assert.Equal(lat, fix.LatitudeDegrees, 5);
        Assert.Equal(lon, fix.LongitudeDegrees, 5);
    }

    [Theory]
    [InlineData("KJ5SKJ: @HB HEARTBEAT EM13", "js8", "KJ5SKJ", "EM13")]
    [InlineData("KJ5SKJ: @ALLCALL CQ EM13", "js8", "KJ5SKJ", "EM13")]
    [InlineData("KJ5SKJ: EM13", "js8", "KJ5SKJ", "EM13")]
    [InlineData("  -9 dB  1500 Hz  KJ5SKJ: @HB HEARTBEAT EM13dd", "js8", "KJ5SKJ", "EM13DD")]
    public void Parses_js8_heartbeat_and_compound_grids(string line, string mode, string call, string grid)
    {
        Assert.True(DigiLocation.TryParse(line, mode, out DigiLocationFix fix));
        Assert.Equal(call, fix.Callsign);
        Assert.Equal(grid, fix.Grid);
        Assert.True(Maidenhead.TryParse(grid, out double lat, out double lon));
        Assert.Equal(lat, fix.LatitudeDegrees, 5);
        Assert.Equal(lon, fix.LongitudeDegrees, 5);
    }

    [Fact]
    public void Parses_wspr_beacon()
    {
        Assert.True(DigiLocation.TryParse(
            "  -15 dB  1402 Hz  KJ5SKJ EM13 37",
            "wspr",
            out DigiLocationFix fix));
        Assert.Equal("KJ5SKJ", fix.Callsign);
        Assert.Equal("EM13", fix.Grid);
        Assert.Equal("wspr", fix.Source);
    }

    [Fact]
    public void Parses_olivia_free_text_with_grid()
    {
        Assert.True(DigiLocation.TryParse(
            "de KJ5SKJ EM13 hello from Texas",
            "olivia",
            out DigiLocationFix fix));
        Assert.Equal("KJ5SKJ", fix.Callsign);
        Assert.Equal("EM13", fix.Grid);
        Assert.Equal("olivia", fix.Source);
    }

    [Theory]
    [InlineData("CQ KJ5SKJ", "ft8")]
    [InlineData("K1ABC W9XYZ R-12", "ft8")]
    [InlineData("K1ABC W9XYZ RR73", "ft8")]
    [InlineData("TU 73", "ft8")]
    public void Rejects_lines_without_location(string line, string mode)
    {
        Assert.False(DigiLocation.TryParse(line, mode, out _));
    }
}
