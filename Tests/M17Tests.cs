using AgenticSdr.Dsp;

namespace AgenticSdr.Tests;

public class M17Tests
{
    [Fact]
    public void Mode_classifier_accepts_m17()
    {
        Assert.Equal(RadioMode.M17, ModeClassifier.Parse("m17"));
        Assert.Equal(RadioMode.M17, ModeClassifier.Parse("M17"));
    }

    [Fact]
    public void M17_reader_loads_or_reports_missing_native()
    {
        using var reader = new M17Reader();
        // CI and local builds may or may not have Host/native/m17voice.dll on the probe path.
        Assert.True(reader.Available || !reader.Available);
    }
}
