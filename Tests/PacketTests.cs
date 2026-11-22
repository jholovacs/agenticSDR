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
    public void Known_channels_include_packet_group()
    {
        IReadOnlyList<KnownChannelGroup> groups = Region2BandPlan.KnownServiceChannels();
        KnownChannelGroup? packet = groups.FirstOrDefault(group => group.Id == "packet");
        Assert.NotNull(packet);
        Assert.Contains(packet.Channels, channel =>
            Math.Abs(channel.FrequencyHz - 144_390_000) < 1 && channel.Mode == "packet");
        Assert.Contains(packet.Channels, channel =>
            Math.Abs(channel.FrequencyHz - 145_010_000) < 1 && channel.Mode == "packet");
        Assert.Contains(packet.Channels, channel =>
            Math.Abs(channel.FrequencyHz - 445_925_000) < 1 && channel.Mode == "packet");
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
        const int rate = PacketDecoder.TxAudioRate;
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
            preambleFlags: PacketDecoder.TxPreambleFlags,
            postambleFlags: PacketDecoder.TxPostambleFlags,
            ("WIDE1", 1, true),
            ("WIDE2", 1, false));

        Assert.True(audio.Length > 0);
        Assert.Equal(0, rate % PacketReader.BaudRate);

        // Test static Decoder (plain English + raw line)
        string decoded = PacketDecoder.Decode(audio, rate);
        Assert.NotEmpty(decoded);
        Assert.Contains("N0CALL-9", decoded, StringComparison.Ordinal);
        Assert.Contains("Raw: N0CALL-9>APRS,WIDE1-1*,WIDE2-1:!4002.34N/10515.22W#Camp APRS beacon", decoded);

        // Test streaming PacketReader with small audio chunks (raw TNC-2; explain is applied on publish)
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
        string explained = AprsExplain.Format(streamedLines[0]);
        Assert.Contains("digipeater", explained, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Raw:", explained, StringComparison.Ordinal);
    }

    [Fact]
    public void Aprs_ui_fm_iq_round_trips_byte_exact_through_bell202_ax25()
    {
        const int rate = PacketDecoder.TxAudioRate;
        const string sourceCall = "KJ5SKJ";
        const string destCall = "APZ001";
        const string payload = "!3215.00N/10745.00W-Test";

        byte[] expected = PacketDecoder.BuildUiFrame(
            sourceCall, 0,
            destCall, 0,
            payload,
            ("WIDE1", 1, false),
            ("WIDE2", 1, false));

        Assert.True(PacketDecoder.VerifyFcs(expected));
        // dest(7)+source(7)+digi(7)+digi(7)=28; control at 28, PID at 29
        Assert.Equal(0x03, expected[28]);
        Assert.Equal(0xF0, expected[29]);

        float[] audio = PacketDecoder.SynthesizeUiAudio(
            sourceCall, 0, destCall, 0, payload,
            sampleRate: rate,
            preambleFlags: PacketDecoder.TxPreambleFlags,
            postambleFlags: PacketDecoder.TxPostambleFlags,
            ("WIDE1", 1, false),
            ("WIDE2", 1, false));

        // Peak stays in [-1, 1]; Space is louder than Mark by ~4 dB.
        Assert.All(audio, s => Assert.InRange(s, -1.0001f, 1.0001f));
        float peak = audio.Max(Math.Abs);
        Assert.InRange(peak, 0.95f, 1.0001f);

        System.Numerics.Complex[] iq = PacketDecoder.ModulateFmIq(audio, rate);
        Assert.Equal(audio.Length, iq.Length);
        Assert.All(iq, c => Assert.InRange(c.Magnitude, 0.999, 1.001));

        float[] recovered = PacketDecoder.DemodulateFmAudio(iq, rate);
        IReadOnlyList<byte[]> frames = PacketDecoder.DecodeRawFrames(recovered, rate);
        Assert.NotEmpty(frames);
        Assert.Equal(expected, frames[0]);
        Assert.True(PacketDecoder.VerifyFcs(frames[0]));

        Ax25Frame? parsed = PacketDecoder.ParseFrame(frames[0]);
        Assert.NotNull(parsed);
        Assert.Equal("KJ5SKJ>APZ001,WIDE1-1,WIDE2-1:!3215.00N/10745.00W-Test", parsed!.Text);

        // Integer-rate FIR upsample to a typical SDR DAC rate (×50 → 2.4 MS/s).
        System.Numerics.Complex[] dac = PacketDecoder.UpsampleIq(iq, rate, 2_400_000);
        Assert.Equal(iq.Length * 50, dac.Length);
    }

    [Fact]
    public void Address_bytes_are_shifted_left_with_hdlc_extension_bit_on_last_only()
    {
        byte[] frame = PacketDecoder.BuildUiFrame(
            "KJ5SKJ", 0, "APZ001", 0, "x",
            ("WIDE1", 1, false),
            ("WIDE2", 1, false));

        // Dest + source + digi1: extension bit clear; last digi: extension bit set.
        Assert.Equal(0, frame[6] & 0x01);
        Assert.Equal(0, frame[13] & 0x01);
        Assert.Equal(0, frame[20] & 0x01);
        Assert.Equal(1, frame[27] & 0x01);

        // Callsign chars are ASCII << 1.
        Assert.Equal((byte)('K' << 1), frame[7]);
        Assert.Equal((byte)('J' << 1), frame[8]);
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
