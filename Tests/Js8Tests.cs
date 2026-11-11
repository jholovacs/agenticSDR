using AgenticSdr.Dsp;

namespace AgenticSdr.Tests;

/// <summary>JS8 Normal-speed checks: published reference frames, message packing, and audio round trips.</summary>
public class Js8Tests
{
    private static byte[] Hex(string hex) => Convert.FromHexString(hex);

    [Fact]
    public void Heartbeat_reference_frame_has_published_tones_and_text()
    {
        byte[] message = Hex("0a2fb3a3ee2ee6ea58");
        byte[] tones = Js8Decoder.Tones(message, Js8Text.TxFirst | Js8Text.TxLast);
        Assert.Equal(
            "4256130" + "40632072007407207412362012277" + "4256130" + "02427663507670567156513030736" + "4256130",
            string.Concat(tones));
        Assert.Equal("KN4CRD: @ALLCALL CQ CQ CQ EM73", Js8Text.Render(message, 3));
    }

    [Fact]
    public void Directed_reference_frame_renders()
    {
        Assert.Equal("KN4CRD: J1Y SNR?", Js8Text.Render(Hex("71717ebdedc10da000"), 3));
    }

    [Theory]
    [InlineData("K1ABC: @ALLCALL CQ CQ CQ FN42")]
    [InlineData("K1ABC: @ALLCALL CQ DX")]
    [InlineData("W9XYZ/P: @HB HEARTBEAT EM73")]
    [InlineData("K1ABC: W9XYZ SNR -12")]
    [InlineData("K1ABC: W9XYZ HEARTBEAT SNR +05")]
    [InlineData("K1ABC: @JS8NET GRID?")]
    [InlineData("K1ABC: J1Y QSL")]
    [InlineData("HELLO WORLD")]
    [InlineData("73 DE K1ABC")]
    public void Frames_pack_and_render_back(string text)
    {
        var message = new byte[9];
        Assert.True(Js8Text.TryPack(text, null, message, out int i3));
        Assert.Equal(3, i3);
        Assert.Equal(text, Js8Text.Render(message, i3));
    }

    [Fact]
    public void Bare_cq_uses_the_given_callsign()
    {
        var message = new byte[9];
        Assert.True(Js8Text.TryPack("CQ CQ EM73", "kn4crd", message, out _));
        Assert.Equal("KN4CRD: @ALLCALL CQ CQ EM73", Js8Text.Render(message, 3));
    }

    [Fact]
    public void Text_that_does_not_fit_one_frame_is_refused()
    {
        var message = new byte[9];
        Assert.False(Js8Text.TryPack("THIS LINE IS FAR TOO LONG FOR A SINGLE JS8 FRAME", null, message, out _));
        Assert.False(Js8Text.TryPack("LOWER CASE # IS NOT IN THE TABLE", null, message, out _));
    }

    [Fact]
    public void Jsc_frames_show_common_words_and_mark_the_rest()
    {
        // Compressed data frame: 1, H=1, then BEACON (dictionary 82), a space flag, a made-up word, padding.
        string bits = "11" + "01111000" + "0101" + "1" + "0111" + "1111" + "0000" + "0" + "0";
        bits = bits.PadRight(72, '1');
        Assert.Equal(72, bits.Length);
        var message = new byte[9];
        for (int i = 0; i < 72; i++)
        {
            if (bits[i] == '1')
                message[i >> 3] |= (byte)(0x80 >> (i & 7));
        }

        string text = Js8Text.Render(message, 3);
        Assert.StartsWith("BEACON ", text, StringComparison.Ordinal);
        Assert.Contains("[#", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("K1ABC: W9XYZ SNR -12", 1_500.0, 0.5)]
    [InlineData("HELLO WORLD", 800.0, 1.0)]
    public void Js8_audio_round_trips(string text, double hz, double start)
    {
        var audio = new float[15 * Js8Decoder.SampleRate];
        Js8Decoder.Modulate(audio, Js8Decoder.SampleRate, text, hz, start);
        IReadOnlyList<Js8Message> found = Js8Decoder.Decode(audio, Js8Decoder.SampleRate);
        Assert.Contains(found, message => message.Text == text && Math.Abs(message.AudioHz - hz) < 10);
    }

    [Fact]
    public void Js8_decodes_two_stations_and_survives_noise()
    {
        var audio = new float[15 * Js8Decoder.SampleRate];
        Js8Decoder.Modulate(audio, Js8Decoder.SampleRate, "K1ABC: @ALLCALL CQ CQ CQ FN42", 1_000, 0.5);
        Js8Decoder.Modulate(audio, Js8Decoder.SampleRate, "W9XYZ: K1ABC SNR -07", 1_900, 0.6);

        // About -13 dB in a 2.5 kHz bandwidth for each station.
        var random = new Random(8);
        for (int i = 0; i < audio.Length; i++)
        {
            double u1 = 1 - random.NextDouble();
            double u2 = random.NextDouble();
            audio[i] += (float)(5.0 * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2));
        }

        IReadOnlyList<Js8Message> found = Js8Decoder.Decode(audio, Js8Decoder.SampleRate);
        Assert.Contains(found, message => message.Text == "K1ABC: @ALLCALL CQ CQ CQ FN42");
        Assert.Contains(found, message => message.Text == "W9XYZ: K1ABC SNR -07");
    }

    [Fact]
    public void Js8_silence_and_short_audio_return_nothing()
    {
        Assert.Empty(Js8Decoder.Decode(new float[15 * Js8Decoder.SampleRate], Js8Decoder.SampleRate));
        Assert.Empty(Js8Decoder.Decode(new float[5 * Js8Decoder.SampleRate], Js8Decoder.SampleRate));
        Assert.Equal("", Js8Decoder.Format([]));
    }
}
