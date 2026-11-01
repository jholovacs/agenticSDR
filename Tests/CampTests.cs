using System.Numerics;
using AgenticSdr.Dsp;
using AgenticSdr.Host;

namespace AgenticSdr.Tests;

/// <summary>Schedule windows have to leave the radio free, and a station has to stand out before camp opens the audio.</summary>
public class CampTests
{
    [Fact]
    public void Overlapping_times_on_the_same_day_conflict()
    {
        CampEntry first = Entry("a", "18:00", "19:00");
        CampEntry second = Entry("b", "18:30", "19:30");
        Assert.True(CampSchedule.Overlaps(first, second));
    }

    [Fact]
    public void Adjacent_windows_and_different_days_do_not_conflict()
    {
        CampEntry evening = Entry("a", "18:00", "19:00");
        CampEntry later = Entry("b", "19:00", "20:00");
        CampEntry sunday = Entry("c", "18:00", "19:00", [0]);
        Assert.False(CampSchedule.Overlaps(evening, later));
        Assert.False(CampSchedule.Overlaps(sunday, evening with { Days = [1, 2, 3, 4, 5] }));
    }

    [Fact]
    public void An_overnight_window_conflicts_with_the_next_morning()
    {
        CampEntry night = Entry("a", "22:00", "02:00", [5]);
        CampEntry early = Entry("b", "01:00", "01:30", [6]);
        CampEntry after = Entry("c", "02:00", "03:00", [6]);
        Assert.True(CampSchedule.Overlaps(night, early));
        Assert.False(CampSchedule.Overlaps(night, after));
    }

    [Fact]
    public void Only_one_open_watch_is_allowed()
    {
        CampEntry standing = Entry("a", null, null);
        CampEntry other = Entry("b", null, null);
        CampEntry timed = Entry("c", "12:00", "13:00");
        Assert.True(CampSchedule.Overlaps(standing, other));
        Assert.False(CampSchedule.Overlaps(standing, timed));
    }

    [Fact]
    public void A_timed_entry_wins_over_an_open_watch()
    {
        var entries = new List<CampEntry>
        {
            Entry("open", null, null, frequency: 146_520_000),
            Entry("noon", "12:00", "13:00", frequency: 162_550_000)
        };
        var during = new DateTime(2026, 11, 2, 12, 15, 0);
        var after = new DateTime(2026, 11, 2, 13, 0, 0);
        Assert.Equal("noon", CampSchedule.ActiveTimed(entries, during)!.Id);
        Assert.Null(CampSchedule.ActiveTimed(entries, after));
    }

    [Fact]
    public void The_book_rejects_an_overlap_and_keeps_a_valid_edit()
    {
        string path = Path.Combine(Path.GetTempPath(), "agentic-sdr-camp-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var book = new CampBook(path);
            CampEntry first = book.Add(Draft(162_550_000, "nbfm", "18:00", "19:00"));
            Assert.Throws<ArgumentException>(() => book.Add(Draft(146_520_000, "nbfm", "18:30", "18:45")));
            CampEntry moved = book.Update(first.Id, Draft(162_550_000, "nbfm", "05:00", "05:30", "NOAA"));
            Assert.Equal("05:00", moved.Start);
            Assert.Equal("NOAA", moved.Label);
            book.Add(Draft(146_520_000, "fmnb", "18:00", "19:00"));
            Assert.Equal(2, book.View(DateTime.Now).Entries.Count);

            var again = new CampBook(path);
            Assert.Equal(2, again.View(DateTime.Now).Entries.Count);
            Assert.Contains(again.View(DateTime.Now).Entries, entry => entry.Label == "NOAA");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Camp_now_starts_right_away_and_can_yield_to_the_schedule()
    {
        string path = Path.Combine(Path.GetTempPath(), "agentic-sdr-camp-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var book = new CampBook(path);
            book.Add(Draft(162_550_000, "nbfm", "00:00", "23:59"));
            var noon = new DateTime(2026, 11, 2, 12, 0, 0);
            CampView view = book.CampNow(Draft(146_520_000, "nbfm", null, null, "Calling"), noon);
            Assert.Equal("camp", view.Mode);
            Assert.Equal(146_520_000, book.Desired(noon)!.FrequencyHz);
            Assert.NotNull(view.Due);
            Assert.Contains("Staying on", view.Note);

            book.SetYieldToSchedule(true);
            Assert.Equal(162_550_000, book.Desired(noon)!.FrequencyHz);
            Assert.Contains("Returns to", book.View(noon).Note);

            book.IgnoreSchedule(noon);
            Assert.Equal(146_520_000, book.Desired(noon)!.FrequencyHz);

            book.FollowSchedule(noon);
            Assert.Equal(162_550_000, book.Desired(noon)!.FrequencyHz);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void A_tone_on_the_station_is_activity_and_noise_is_not()
    {
        const double rate = 48_000;
        Complex[] noise = Noise(12_000, 0.05, 7);
        Assert.False(ChannelActivity.Heard(noise, rate, 8_000, 1_000));

        Complex[] tone = Tone(12_000, rate, 8_000, 0.8);
        Assert.True(ChannelActivity.Heard(tone, rate, 8_000, 1_000));
        Assert.False(ChannelActivity.Heard(tone, rate, 1_000, 1_000));
        Assert.True(ChannelActivity.Heard(noise, rate, 8_000, 1_000, squelch: 0));
        Assert.False(ChannelActivity.Heard(noise, rate, 8_000, 1_000, squelch: 50));
    }

    [Fact]
    public void Squelch_zero_is_open_and_fifty_matches_the_old_default()
    {
        Assert.Equal(0, ChannelActivity.RatioForSquelch(0));
        Assert.InRange(ChannelActivity.RatioForSquelch(50), 7, 9);
        Assert.True(ChannelActivity.RatioForSquelch(100) > ChannelActivity.RatioForSquelch(50));
    }

    [Fact]
    public void Measure_reports_higher_snr_on_the_tone_than_on_noise()
    {
        const double rate = 48_000;
        (_, double noiseSnr) = ChannelActivity.Measure(Noise(12_000, 0.05, 7), rate, 8_000, 1_000);
        (_, double toneSnr) = ChannelActivity.Measure(Tone(12_000, rate, 8_000, 0.8), rate, 8_000, 1_000);
        Assert.True(toneSnr > noiseSnr + 10);
    }

    [Fact]
    public void Activity_stays_open_through_a_short_dip()
    {
        var hold = new ActivityHold();
        hold.Observe(true, 1000, 48_000);
        Assert.True(hold.Active);
        hold.Observe(false, 1000, 48_000);
        Assert.True(hold.Active);
        hold.Observe(false, 48_000, 48_000);
        Assert.False(hold.Active);
    }

    private static CampEntry Entry(string id, string? start, string? end, int[]? days = null, double frequency = 146_520_000) =>
        new(id, null, frequency, "nbfm", start, end, days, null, null, null);

    private static CampDraft Draft(double frequencyHz, string mode, string? start, string? end, string? label = null) => new()
    {
        FrequencyHz = frequencyHz,
        Mode = mode,
        Start = start,
        End = end,
        Label = label
    };

    private static Complex[] Tone(int count, double rate, double frequency, double amplitude)
    {
        var iq = new Complex[count];
        for (int i = 0; i < count; i++)
            iq[i] = Complex.FromPolarCoordinates(amplitude, 2 * Math.PI * frequency * i / rate);
        return iq;
    }

    private static Complex[] Noise(int count, double amplitude, int seed)
    {
        var random = new Random(seed);
        var iq = new Complex[count];
        for (int i = 0; i < count; i++)
            iq[i] = new Complex((random.NextDouble() * 2 - 1) * amplitude, (random.NextDouble() * 2 - 1) * amplitude);
        return iq;
    }
}
