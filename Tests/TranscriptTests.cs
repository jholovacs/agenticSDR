using AgenticSdr.Host;

namespace AgenticSdr.Tests;

/// <summary>Morse text grows as the decoder re-reads a moving window. Older letters stay.</summary>
public class TranscriptTests
{
    [Fact]
    public void A_longer_decode_keeps_what_was_already_shown()
    {
        Assert.Equal("CQ DE", TranscriptLog.Grow("CQ", "CQ", "CQ DE"));
    }

    [Fact]
    public void Letters_that_scroll_out_of_the_window_stay_on_the_line()
    {
        Assert.Equal("CQ DE W1", TranscriptLog.Grow("CQ DE", "CQ DE", "Q DE W1"));
    }

    [Fact]
    public void A_new_listen_appends_instead_of_replacing()
    {
        Assert.Equal("CQ DE K", TranscriptLog.Grow("CQ", "", "DE K"));
    }
}
