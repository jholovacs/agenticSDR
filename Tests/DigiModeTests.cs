using System.Numerics;
using AgenticSdr.Dsp;
using AgenticSdr.Host;

namespace AgenticSdr.Tests;

/// <summary>Checks JS8/Olivia/JTTY plumbing: dials, classifiers, and decoder smoke paths.</summary>
public class DigiModeTests
{
    [Theory]
    [InlineData(14_078_000, RadioMode.Js8)]
    [InlineData(14_071_500, RadioMode.Olivia)]
    [InlineData(14_090_000, RadioMode.Jtty)]
    [InlineData(14_105_000, RadioMode.Varac)]
    [InlineData(28_078_000, RadioMode.Js8)]
    [InlineData(28_105_000, RadioMode.Varac)]
    public void Digi_dials_resolve(double frequencyHz, RadioMode mode)
    {
        Assert.True(Region2BandPlan.TryDigiDial(frequencyHz, mode, out double dial));
        Assert.Equal(frequencyHz, dial);
        Assert.True(Region2BandPlan.TryAnyDigiDial(frequencyHz, out RadioMode found, out _));
        Assert.Equal(mode, found);
    }

    [Fact]
    public void Mode_parse_accepts_js8_olivia_jtty()
    {
        Assert.Equal(RadioMode.Js8, ModeClassifier.Parse("js8"));
        Assert.Equal(RadioMode.Js8, ModeClassifier.Parse("js8call"));
        Assert.Equal(RadioMode.Olivia, ModeClassifier.Parse("olivia"));
        Assert.Equal(RadioMode.Jtty, ModeClassifier.Parse("jtty"));
        Assert.Equal(RadioMode.Varac, ModeClassifier.Parse("varac"));
        Assert.Equal(RadioMode.Varac, ModeClassifier.Parse("zara"));
        Assert.Equal(RadioMode.Varac, ModeClassifier.Parse("xara"));
        Assert.Equal("js8", CampSchedule.Canonical(RadioMode.Js8));
        Assert.Equal("olivia", CampSchedule.Canonical(RadioMode.Olivia));
        Assert.Equal("jtty", CampSchedule.Canonical(RadioMode.Jtty));
        Assert.Equal("varac", CampSchedule.Canonical(RadioMode.Varac));
    }

    [Fact]
    public void Known_channels_include_digi_groups()
    {
        IReadOnlyList<KnownChannelGroup> groups = Region2BandPlan.KnownServiceChannels();
        Assert.Contains(groups, group => group.Id == "js8" && group.Channels.Count > 0);
        Assert.Contains(groups, group => group.Id == "olivia" && group.Channels.Count > 0);
        Assert.Contains(groups, group => group.Id == "jtty" && group.Channels.Count > 0);
        Assert.Contains(groups, group => group.Id == "varac" && group.Channels.Count > 0);
    }

    [Fact]
    public void Xara_json_lines_parse()
    {
        Assert.True(XaraMonitor.TryParseLine("""{"text":"CQ DE K1ABC","kind":"cq","from":"K1ABC"}""", out XaraMessage message));
        Assert.Contains("CQ", message.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("cq", message.Kind);
        Assert.True(XaraMonitor.TryParseLine("K1ABC: hello VarAC", out XaraMessage plain));
        Assert.Equal("K1ABC: hello VarAC", plain.Text);
    }

    [Fact]
    public void Xara_bundled_monitor_loads_or_explains()
    {
        using var monitor = new XaraMonitor();
        if (monitor.Available)
        {
            Assert.Null(monitor.MissingHint);
            // Silence should not invent text.
            Assert.Empty(XaraMonitor.Decode(new float[12_000], 12_000));
            return;
        }

        Assert.Contains("xaramonitor", monitor.MissingHint ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Js8_modulate_and_decode_recovers_text()
    {
        var audio = new float[15 * Js8Decoder.SampleRate];
        Js8Decoder.Modulate(audio, Js8Decoder.SampleRate, "KN4CRD: @ALLCALL CQ CQ CQ EM73", 1_200, 0.5);
        IReadOnlyList<Js8Message> found = Js8Decoder.Decode(audio, Js8Decoder.SampleRate);
        Js8Message hit = Assert.Single(found);
        Assert.Equal("KN4CRD: @ALLCALL CQ CQ CQ EM73", hit.Text);
        Assert.InRange(hit.AudioHz, 1_190, 1_210);
        Assert.StartsWith("JS8", Js8Decoder.Format(found), StringComparison.Ordinal);
        Assert.Contains("calling anyone", Js8Decoder.Explain(hit.Text), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Js8_decoder_does_not_read_ft8_audio()
    {
        var audio = new float[15 * Ft8Decoder.SampleRate];
        Ft8Decoder.Modulate(audio, Ft8Decoder.SampleRate, "CQ K1ABC FN42", 1_200, 0.5);
        Assert.Empty(Js8Decoder.Decode(audio, Ft8Decoder.SampleRate));
    }

    [Fact]
    public void Js8_survives_tuner_offset_iq_round_trip()
    {
        const double offset = Demodulator.VoiceOffsetHz;
        const double audioHz = 1_200;
        int count = (int)(15.2 * Demodulator.InputRate);
        var iq = new Complex[count];
        var audio = new float[(int)(15.2 * Js8Decoder.SampleRate)];
        Js8Decoder.Modulate(audio, Js8Decoder.SampleRate, "KN4CRD: @ALLCALL CQ CQ CQ EM73", audioHz, 0.5);
        double rfHz = audioHz - offset;
        double upsample = Demodulator.InputRate / Js8Decoder.SampleRate;
        double phase = 0;
        double step = 2 * Math.PI * rfHz / Demodulator.InputRate;
        for (int i = 0; i < count; i++)
        {
            int a = Math.Min(audio.Length - 1, (int)(i / upsample));
            double env = audio[a];
            iq[i] = new Complex(env * Math.Cos(phase), env * Math.Sin(phase));
            phase += step;
            if (phase > Math.PI)
                phase -= 2 * Math.PI;
            else if (phase < -Math.PI)
                phase += 2 * Math.PI;
        }

        DemodResult result = Demodulator.Demodulate(iq, RadioMode.Js8, offsetHz: -offset, normalize: true);
        Assert.Equal(Js8Decoder.SampleRate, result.SampleRate);
        IReadOnlyList<Js8Message> found = Js8Decoder.Decode(result.Audio, result.SampleRate);
        Assert.Contains(found, message => message.Text == "KN4CRD: @ALLCALL CQ CQ CQ EM73");
    }

    [Fact]
    public void Varac_demod_keeps_the_usb_passband_tone()
    {
        int count = (int)(0.25 * Demodulator.InputRate);
        var iq = new Complex[count];
        double phase = 0;
        double step = 2 * Math.PI * 1_500 / Demodulator.InputRate;
        for (int i = 0; i < count; i++)
        {
            iq[i] = new Complex(Math.Cos(phase), Math.Sin(phase));
            phase += step;
            if (phase > Math.PI)
                phase -= 2 * Math.PI;
        }

        DemodResult result = Demodulator.Demodulate(iq, RadioMode.Varac, normalize: false);
        Assert.Equal(Demodulator.OutputRate, result.SampleRate);
        Assert.True(result.Audio.Length > 1_000);
        double tone = Goertzel(result.Audio, result.SampleRate, 1_500);
        double other = Goertzel(result.Audio, result.SampleRate, 500);
        Assert.True(tone > other * 10);
    }

    [Fact]
    public void Xara_format_audio_reports_empty_arq_without_inventing_copy()
    {
        using var monitor = new XaraMonitor();
        Assert.True(monitor.Available, monitor.MissingHint ?? "xara missing");
        string text = XaraMonitor.FormatAudio(new float[PreferredVaracSilence], 12_000);
        Assert.Contains("no complete ARQ", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CQ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Digi_clip_analyzer_reports_ft8_self_test_on_a_wav()
    {
        var audio = new float[15 * Ft8Decoder.SampleRate];
        Ft8Decoder.Modulate(audio, Ft8Decoder.SampleRate, "CQ K1ABC FN42", 1_500, 0.5);
        byte[] wav = WavFile.Write(audio, Ft8Decoder.SampleRate);
        DigiAnalysisReport report = DigiClipAnalyzer.AnalyzeWav(wav, "ft8");
        Assert.True(report.Ft8Count >= 1);
        Assert.True(report.SelfTestOk);
        Assert.Contains(report.Messages, line => line.Text.Contains("K1ABC", StringComparison.Ordinal));
        Assert.NotEmpty(report.Hints);
    }

    private const int PreferredVaracSilence = 12_000;

    private static double Goertzel(float[] audio, int sampleRate, double frequency)
    {
        double real = 0;
        double imag = 0;
        for (int i = 0; i < audio.Length; i++)
        {
            double angle = 2 * Math.PI * frequency * i / sampleRate;
            real += audio[i] * Math.Cos(angle);
            imag += audio[i] * Math.Sin(angle);
        }

        return real * real + imag * imag;
    }

    [Fact]
    public void Jtty_modulate_and_decode_prints_cq()
    {
        var audio = new float[(int)(4 * JttyDecoder.SampleRate)];
        JttyDecoder.Modulate(audio, JttyDecoder.SampleRate, "CQ K1ABC CQ", 1_500, 0.55f);
        IReadOnlyList<JttyMessage> found = JttyDecoder.Decode(audio, JttyDecoder.SampleRate);
        Assert.Contains(found, message => message.Text.Contains("CQ K1ABC CQ", StringComparison.Ordinal));
    }

    [Fact]
    public void Jtty_modulate_and_decode_prints_free_text()
    {
        var audio = new float[(int)(8 * JttyDecoder.SampleRate)];
        JttyDecoder.Modulate(audio, JttyDecoder.SampleRate, "HELLO", 1_500, 0.55f);
        IReadOnlyList<JttyMessage> found = JttyDecoder.Decode(audio, JttyDecoder.SampleRate);
        Assert.Contains(found, message => message.Text.Contains("HELLO", StringComparison.Ordinal));
    }

    [Fact]
    public void Jtty_official_sample_decodes_when_present()
    {
        string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".tmp", "jtty_sample.wav"));
        if (!File.Exists(path))
            return; // optional fixture from WSJT samples/JTTY

        byte[] file = File.ReadAllBytes(path);
        int rate = BitConverter.ToInt32(file, 24);
        int bits = BitConverter.ToInt16(file, 34);
        int channels = BitConverter.ToInt16(file, 22);
        int dataOfs = 44;
        int bytesPer = Math.Max(1, bits / 8 * channels);
        int frames = (file.Length - dataOfs) / bytesPer;
        var mono = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            short s = BitConverter.ToInt16(file, dataOfs + i * bytesPer);
            mono[i] = s / 32768f;
        }

        IReadOnlyList<JttyMessage> found = JttyDecoder.Decode(mono, rate);
        Assert.NotEmpty(found);
        string joined = string.Join(' ', found.Select(m => m.Text));
        Assert.True(
            joined.Contains("RAN", StringComparison.Ordinal) || joined.Contains("DECODE", StringComparison.Ordinal),
            $"unexpected JTTY sample text: {joined}");
    }

    [Fact]
    public void Digi_activity_confidence_stays_below_decode_certainty()
    {
        var score = new Ft8Score(true, 12, 0.4, 8, 3);
        double confidence = DigiActivity.ActivityConfidence(score);
        Assert.InRange(confidence, 0.28, 0.52);
    }

    [Fact]
    public void Classifier_digi_hint_confidence_is_activity_not_decode()
    {
        var peak = new SpectralPeak(14_078_500, -40, -50, 12, 2_400, 14_078_000, 14_081_000, 0, 0.1, false);
        Classification hit = ModeClassifier.Classify(peak, new BandHint(RadioMode.Js8, CallingName: "20 m JS8"));
        Assert.Equal(RadioMode.Js8, hit.Mode);
        Assert.True(hit.Confidence < 0.60, $"digi confidence {hit.Confidence} looked like a decode claim");
    }

    [Fact]
    public void Olivia_silence_returns_nothing()
    {
        var audio = new float[OliviaDecoder.SampleRate * 2];
        Assert.Empty(OliviaDecoder.Decode(audio, OliviaDecoder.SampleRate));
    }

    [Fact]
    public void Olivia_round_trip_recovers_keyboard_text()
    {
        var profile = new OliviaProfile(8, 250, "8/250");
        float[] wave = OliviaDecoder.Modulate(
            "CQ DE K7ABC", profile, OliviaDecoder.SampleRate, centerHz: OliviaDecoder.CenterHz);
        IReadOnlyList<OliviaMessage> messages = OliviaDecoder.Decode(wave, OliviaDecoder.SampleRate);
        Assert.NotEmpty(messages);
        Assert.Contains("CQ DE K7ABC", messages[0].Text, StringComparison.Ordinal);
        Assert.Equal("8/250", messages[0].Profile);
    }

    [Fact]
    public void Olivia_recovers_text_when_audio_center_is_mistuned()
    {
        var profile = new OliviaProfile(8, 250, "8/250");
        // Simulate an RTL that is ~220 Hz high: stack lands below the nominal 1500 Hz center.
        float[] wave = OliviaDecoder.Modulate(
            "HELLO", profile, OliviaDecoder.SampleRate, centerHz: OliviaDecoder.CenterHz - 220);
        IReadOnlyList<OliviaMessage> messages = OliviaDecoder.Decode(wave, OliviaDecoder.SampleRate);
        Assert.NotEmpty(messages);
        Assert.Contains("HELLO", messages[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Dial_frequency_estimates_band_offset()
    {
        var profile = new OliviaProfile(8, 250, "8/250");
        float[] wave = OliviaDecoder.Modulate("TEST", profile, OliviaDecoder.SampleRate, centerHz: 1_300);
        double offset = DialFrequency.EstimateBandOffsetHz(
            wave, OliviaDecoder.SampleRate, 1_500, OliviaDecoder.BandwidthHz * 0.5, 400);
        Assert.InRange(offset, -250, -150);
    }

    [Fact]
    public void Published_station_matches_known_olivia_and_noaa_dials()
    {
        Assert.True(Region2BandPlan.TryPublishedStation(3_581_500, RadioMode.Olivia, out double olivia, out RadioMode oliviaMode));
        Assert.Equal(3_581_500, olivia);
        Assert.Equal(RadioMode.Olivia, oliviaMode);

        Assert.True(Region2BandPlan.TryPublishedStation(162_550_000, RadioMode.Nbfm, out double noaa, out RadioMode noaaMode));
        Assert.Equal(162_550_000, noaa);
        Assert.Equal(RadioMode.Nbfm, noaaMode);
    }

    [Fact]
    public void Olivia_noise_does_not_invent_chat()
    {
        var rng = new Random(42);
        var audio = new float[OliviaDecoder.SampleRate * 8];
        for (int i = 0; i < audio.Length; i++)
            audio[i] = (float)(rng.NextDouble() * 2 - 1) * 0.2f;
        Assert.Empty(OliviaDecoder.Decode(audio, OliviaDecoder.SampleRate));
    }

    [Fact]
    public void Classifier_prefers_js8_hint_on_narrow_peak()
    {
        var peak = new SpectralPeak(14_078_500, -40, -50, 10, 2_400, 14_078_000, 14_081_000, 0, 0.1, false);
        Classification hit = ModeClassifier.Classify(peak, new BandHint(RadioMode.Js8, CallingName: "20 m JS8"));
        Assert.Equal(RadioMode.Js8, hit.Mode);
    }
}
