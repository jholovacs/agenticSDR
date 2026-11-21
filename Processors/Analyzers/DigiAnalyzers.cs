using AgenticSdr.Dsp;

namespace AgenticSdr.Processors;

public sealed class Ft8Analyzer : DigiUsbAnalyzer
{
    public Ft8Analyzer() : base("ft8", RadioMode.Ft8, RadioMode.Ft8) { }

    protected override (bool Ok, string Detail) TryConfirm(float[] audio, int rate)
    {
        var messages = Ft8Decoder.Decode(audio, rate);
        return messages.Count > 0
            ? (true, $"{messages.Count} FT8 message(s) in the short capture.")
            : (false, "FT8 envelope/dial fit; no full-slot decode in this buffer.");
    }
}

public sealed class Js8Analyzer : DigiUsbAnalyzer
{
    public Js8Analyzer() : base("js8", RadioMode.Js8, RadioMode.Js8) { }

    protected override (bool Ok, string Detail) TryConfirm(float[] audio, int rate)
    {
        var messages = Js8Decoder.Decode(audio, rate);
        return messages.Count > 0
            ? (true, $"{messages.Count} JS8 message(s) in the short capture.")
            : (false, "JS8 envelope/dial fit; no full-slot decode in this buffer.");
    }
}

public sealed class WsprAnalyzer : DigiUsbAnalyzer
{
    public WsprAnalyzer() : base("wspr", RadioMode.Wspr, RadioMode.Wspr) { }

    protected override (bool Ok, string Detail) TryConfirm(float[] audio, int rate)
    {
        var messages = WsprDecoder.Decode(audio, rate);
        return messages.Count > 0
            ? (true, $"WSPR beacon: {messages[0].Text}.")
            : (false, "WSPR envelope/dial fit; no Type-1 decode in this short buffer.");
    }
}

public sealed class OliviaAnalyzer : DigiUsbAnalyzer
{
    public OliviaAnalyzer() : base("olivia", RadioMode.Olivia, RadioMode.Olivia) { }

    protected override (bool Ok, string Detail) TryConfirm(float[] audio, int rate)
    {
        string text = OliviaDecoder.Format(OliviaDecoder.Decode(audio, rate));
        return !string.IsNullOrWhiteSpace(text)
            ? (true, "Olivia text decoded from the capture.")
            : (false, "Olivia envelope/dial fit; no confident text yet.");
    }
}

public sealed class JttyAnalyzer : DigiUsbAnalyzer
{
    public JttyAnalyzer() : base("jtty", RadioMode.Jtty, RadioMode.Jtty) { }

    protected override (bool Ok, string Detail) TryConfirm(float[] audio, int rate)
    {
        string text = JttyDecoder.Format(JttyDecoder.Decode(audio, rate));
        return !string.IsNullOrWhiteSpace(text)
            ? (true, "JTTY text decoded from the capture.")
            : (false, "JTTY envelope/dial fit; no text yet.");
    }
}

public sealed class VaracAnalyzer : DigiUsbAnalyzer
{
    public VaracAnalyzer() : base("varac", RadioMode.Varac, RadioMode.Usb) { }

    protected override (bool Ok, string Detail) TryConfirm(float[] audio, int rate)
    {
        string text = XaraMonitor.FormatAudio(audio, rate);
        if (!string.IsNullOrWhiteSpace(text) && !text.Contains("missing", StringComparison.OrdinalIgnoreCase))
            return (true, "VarAC monitor decoded text.");
        return (false, "VarAC envelope/dial fit; monitor quiet or unavailable.");
    }
}

public sealed class PacketAnalyzer : IModeAnalyzer
{
    public string Id => "packet";
    public RadioMode Mode => RadioMode.Packet;

    public ModeAnalysis Analyze(in ModeAnalysisContext context)
    {
        double score = AnalyzerHelpers.EnvelopeScore(Mode, context.Peak);
        score += AnalyzerHelpers.HintBoost(context, Mode);
        if (context.PublishedDialHz is not null)
            score += 0.10;

        DemodResult? demod = AnalyzerHelpers.TryDemod(context, Mode);
        if (demod is not null)
        {
            string text = PacketDecoder.Decode(AnalyzerHelpers.Mono(demod), demod.SampleRate);
            if (!string.IsNullOrWhiteSpace(text))
            {
                score = Math.Max(score, 0.88);
                return AnalyzerHelpers.Result(Mode, score, context, "AX.25/APRS frame decoded.", true);
            }
        }

        return AnalyzerHelpers.Result(Mode, score, context,
            score >= 0.4 ? "NBFM envelope near a packet dial; no frame in this buffer." : "Weak fit to packet.");
    }
}

public sealed class RibbitAnalyzer : IModeAnalyzer
{
    public string Id => "ribbit";
    public RadioMode Mode => RadioMode.Ribbit;

    public ModeAnalysis Analyze(in ModeAnalysisContext context)
    {
        double score = AnalyzerHelpers.EnvelopeScore(Mode, context.Peak);
        score += AnalyzerHelpers.HintBoost(context, Mode);
        if (context.PublishedDialHz is not null)
            score += 0.08;

        DemodResult? demod = AnalyzerHelpers.TryDemod(context, Mode);
        if (demod is not null)
        {
            RibbitHit hit = RibbitDecoder.Detect(AnalyzerHelpers.Mono(demod), demod.SampleRate);
            if (hit.Active)
            {
                score = Math.Max(score, 0.80);
                return AnalyzerHelpers.Result(Mode, score, context, "Ribbit OFDM comb detected.", true);
            }
        }

        return AnalyzerHelpers.Result(Mode, score, context,
            score >= 0.4 ? "NBFM envelope; no Ribbit comb in this buffer." : "Weak fit to Ribbit.");
    }
}

public abstract class DigiUsbAnalyzer(string id, RadioMode mode, RadioMode demodMode) : IModeAnalyzer
{
    public string Id { get; } = id;
    public RadioMode Mode { get; } = mode;

    public ModeAnalysis Analyze(in ModeAnalysisContext context)
    {
        double score = AnalyzerHelpers.EnvelopeScore(Mode, context.Peak);
        score += AnalyzerHelpers.HintBoost(context, Mode);
        if (context.PublishedDialHz is not null)
            score += 0.12;

        DemodResult? demod = AnalyzerHelpers.TryDemod(context, demodMode);
        if (demod is not null)
        {
            (bool ok, string detail) = TryConfirm(AnalyzerHelpers.Mono(demod), demod.SampleRate);
            if (ok)
            {
                score = Math.Max(score, 0.90);
                return AnalyzerHelpers.Result(Mode, score, context, detail, true);
            }

            return AnalyzerHelpers.Result(Mode, score, context, detail);
        }

        return AnalyzerHelpers.Result(Mode, score, context,
            score >= 0.35 ? $"{Mode} envelope/dial candidate." : $"Weak fit to {Mode}.");
    }

    protected abstract (bool Ok, string Detail) TryConfirm(float[] audio, int rate);
}
