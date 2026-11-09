using System.Numerics;
using AgenticSdr.Dsp;
using AgenticSdr.Host;

namespace AgenticSdr.Tests;

/// <summary>
/// Checks AX.25 frame encoding and decoding, Bell 202 AFSK demodulation, FCS integrity,
/// band-plan hints for APRS calling spots, and camp schedule normalization for packet mode.
/// </summary>
public class PacketTests
{
    [Theory]
    [InlineData("packet")]
    [InlineData("Packet")]
    [InlineData("ax25")]
    [InlineData("AX25")]
    [InlineData("aprs")]
    [InlineData("APRS")]
    [InlineData("afsk")]
    [InlineData("pkt")]
    public void Mode_classifier_accepts_packet_aliases(string mode)
    {
        Assert.Equal(RadioMode.Packet, ModeClassifier.Parse(mode));
    }

    [Fact]
    public void Camp_schedule_canonicalizes_and_normalizes_packet_mode()
    {
        Assert.Equal("packet", CampSchedule.Canonical(RadioMode.Packet));

        var draft = new CampDraft
        {
            FrequencyHz = 144_390_000,
            Mode = "packet",
            Label = "2m APRS"
        };

        CampEntry entry = CampSchedule.Normalize(draft, "aprs", allowOpen: true);
        Assert.Equal("packet", entry.Mode);
        Assert.Equal(144_390_000, entry.FrequencyHz);
        Assert.Equal("2m APRS", entry.Label);
    }

    [Fact]
    public void Two_meter_band_plan_identifies_aprs_packet_calling_frequency()
    {
        BandHint hint = Region2BandPlan.HintAt(144_390_000);
        Assert.Equal(RadioMode.Packet, hint.PreferredMode);
        Assert.Equal("2 m APRS packet", Region2BandPlan.SourceLabel(144_390_000));
    }

    [Fact]
    public void Fcs_computes_and_detects_corrupted_bytes()
    {
        byte[] frame = PacketDecoder.BuildUiFrame(
            "N0CALL", 0,
            "APRS", 0,
            "Hello World");

        Assert.True(PacketDecoder.VerifyFcs(frame));

        // Corrupt one bit
        frame[10] ^= 0x01;
        Assert.False(PacketDecoder.VerifyFcs(frame));
    }

    [Fact]
    public void Ax25_address_encodes_and_decodes_cleanly()
    {
        var buffer = new byte[7];
        PacketDecoder.EncodeAddress(buffer, "K7TRI", 1, isLast: false, repeated: true);
        Ax25Address address = PacketDecoder.ParseAddress(buffer, out bool isLast);

        Assert.Equal("K7TRI", address.Callsign);
        Assert.Equal(1, address.Ssid);
        Assert.True(address.Repeated);
        Assert.False(isLast);
        Assert.Equal("K7TRI-1*", address.Format());
    }

    [Fact]
    public void Bell_202_afsk_audio_demodulates_and_decodes_ax25_frame()
    {
        const int rate = 48_000;
        string sourceCall = "N0CALL";
        int sourceSsid = 9;
        string destCall = "APRS";
        int destSsid = 0;
        string payload = "!4002.34N/10515.22W#Camp APRS beacon";

        float[] audio = PacketDecoder.SynthesizeUiAudio(
            sourceCall, sourceSsid,
            destCall, destSsid,
            payload,
            sampleRate: rate,
            preambleFlags: 20,
            ("WIDE1", 1, true),
            ("WIDE2", 1, false));

        Assert.True(audio.Length > 0);

        // Test static Decoder
        string decoded = PacketDecoder.Decode(audio, rate);
        Assert.NotEmpty(decoded);
        Assert.Contains("N0CALL-9>APRS,WIDE1-1*,WIDE2-1:!4002.34N/10515.22W#Camp APRS beacon", decoded);

        // Test streaming PacketReader with small audio chunks
        var reader = new PacketReader(rate);
        const int chunkSize = 1024;
        var streamedLines = new List<string>();

        for (int offset = 0; offset < audio.Length; offset += chunkSize)
        {
            int len = Math.Min(chunkSize, audio.Length - offset);
            IReadOnlyList<string> lines = reader.Push(audio.AsSpan(offset, len), rate);
            streamedLines.AddRange(lines);
        }

        Assert.Single(streamedLines);
        Assert.Equal("N0CALL-9>APRS,WIDE1-1*,WIDE2-1:!4002.34N/10515.22W#Camp APRS beacon", streamedLines[0]);
    }

    [Fact]
    public void Non_packet_noise_does_not_produce_false_frames()
    {
        const int rate = 48_000;
        var random = new Random(42);
        var noise = new float[rate * 2]; // 2 seconds of noise
        for (int i = 0; i < noise.Length; i++)
            noise[i] = (float)(random.NextDouble() * 0.4 - 0.2);

        string decoded = PacketDecoder.Decode(noise, rate);
        Assert.Empty(decoded);

        var reader = new PacketReader(rate);
        IReadOnlyList<string> lines = reader.Push(noise, rate);
        Assert.Empty(lines);
    }
}
