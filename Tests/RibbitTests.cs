using AgenticSdr.Dsp;
using AgenticSdr.Host;

namespace AgenticSdr.Tests;

public class RibbitTests
{
    [Theory]
    [InlineData("ribbit")]
    [InlineData("Ribbit")]
    [InlineData("rattlegram")]
    [InlineData("ori")]
    public void Mode_classifier_accepts_ribbit_aliases(string mode)
    {
        Assert.Equal(RadioMode.Ribbit, ModeClassifier.Parse(mode));
    }

    [Fact]
    public void Camp_schedule_canonicalizes_ribbit_mode()
    {
        Assert.Equal("ribbit", CampSchedule.Canonical(RadioMode.Ribbit));
        CampEntry entry = CampSchedule.Normalize(
            new CampDraft { FrequencyHz = 146_520_000, Mode = "ribbit", Label = "Ribbit watch" },
            "ribbit",
            allowOpen: true);
        Assert.Equal("ribbit", entry.Mode);
    }

    [Fact]
    public void Synthetic_ofdm_comb_is_detected_and_formatted()
    {
        const int rate = RibbitDecoder.SampleRate;
        float[] audio = RibbitDecoder.SynthesizeToneComb(rate, durationSeconds: 1.0);
        RibbitHit hit = RibbitDecoder.Detect(audio, rate);
        Assert.True(hit.Active);
        Assert.True(hit.CarrierHits >= 120);
        Assert.True(hit.SnrDb >= 10);
        Assert.True(hit.NotchDb >= 5);

        string line = RibbitDecoder.Decode(audio, rate);
        Assert.Contains("Ribbit OFDM", line);
        Assert.Contains("carriers", line);
    }

    [Fact]
    public void Noise_does_not_look_like_ribbit()
    {
        const int rate = RibbitDecoder.SampleRate;
        var random = new Random(7);
        var noise = new float[rate];
        for (int i = 0; i < noise.Length; i++)
            noise[i] = (float)(random.NextDouble() * 0.3 - 0.15);

        Assert.False(RibbitDecoder.Detect(noise, rate).Active);
        Assert.Empty(RibbitDecoder.Decode(noise, rate));
    }

    [Fact]
    public void Streaming_reader_emits_once_when_comb_appears()
    {
        const int rate = RibbitDecoder.SampleRate;
        float[] audio = RibbitDecoder.SynthesizeToneComb(rate, durationSeconds: 1.0);
        var reader = new RibbitReader(rate);
        var lines = new List<string>();
        const int chunk = 2048;
        for (int offset = 0; offset < audio.Length; offset += chunk)
        {
            int len = Math.Min(chunk, audio.Length - offset);
            lines.AddRange(reader.Push(audio.AsSpan(offset, len), rate));
        }

        Assert.NotEmpty(lines);
        Assert.Contains("Ribbit OFDM", lines[0]);
    }

    [Fact]
    public void Packet_dials_include_iss_and_70cm_aprs()
    {
        Assert.Equal(RadioMode.Packet, Region2BandPlan.HintAt(145_825_000).PreferredMode);
        Assert.Equal(RadioMode.Packet, Region2BandPlan.HintAt(445_925_000).PreferredMode);
        Assert.True(Region2BandPlan.TryDigiDial(144_390_000, RadioMode.Packet, out double dial));
        Assert.Equal(144_390_000, dial);
    }

    [Fact]
    public void Classifier_prefers_packet_on_aprs_dial_with_nbfm_width()
    {
        var peak = new SpectralPeak(
            144_390_000,
            -20,
            -40,
            12,
            12_000,
            144_384_000,
            144_396_000,
            2,
            0.05,
            false);
        Classification hit = ModeClassifier.Classify(peak, Region2BandPlan.HintAt(144_390_000));
        Assert.Equal(RadioMode.Packet, hit.Mode);
    }
}
