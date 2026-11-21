using AgenticSdr.Dsp;

namespace AgenticSdr.Processors;

public sealed class CwAnalyzer : IModeAnalyzer
{
    public string Id => "cw";
    public RadioMode Mode => RadioMode.Cw;

    public ModeAnalysis Analyze(in ModeAnalysisContext context)
    {
        double score = AnalyzerHelpers.EnvelopeScore(Mode, context.Peak);
        score += AnalyzerHelpers.HintBoost(context, Mode);
        if (context.Peak.BandwidthHz < 1_000 && context.Peak.SnrDb >= 8)
            score += 0.20;

        DemodResult? demod = AnalyzerHelpers.TryDemod(context, Mode);
        if (demod is not null)
        {
            string text = MorseDecoder.DecodeAudible(AnalyzerHelpers.Mono(demod), demod.SampleRate);
            if (MorseDecoder.IsCopy(text))
            {
                score = Math.Max(score, 0.82);
                string shown = text.Trim();
                if (shown.Length > 40)
                    shown = shown[..40] + "…";
                return AnalyzerHelpers.Result(Mode, score, context, $"CW envelope fits; Morse copy “{shown}”.", true);
            }
        }

        return AnalyzerHelpers.Result(Mode, score, context,
            score >= 0.4 ? "Narrow peak fits CW envelope." : "Weak fit to CW envelope.");
    }
}

public sealed class UsbAnalyzer : IModeAnalyzer
{
    public string Id => "usb";
    public RadioMode Mode => RadioMode.Usb;

    public ModeAnalysis Analyze(in ModeAnalysisContext context) =>
        SidebandLogic.SidebandAnalyze(Mode, context, preferPositiveAsymmetry: true);
}

public sealed class LsbAnalyzer : IModeAnalyzer
{
    public string Id => "lsb";
    public RadioMode Mode => RadioMode.Lsb;

    public ModeAnalysis Analyze(in ModeAnalysisContext context) =>
        SidebandLogic.SidebandAnalyze(Mode, context, preferPositiveAsymmetry: false);
}

public sealed class AmAnalyzer : IModeAnalyzer
{
    public string Id => "am";
    public RadioMode Mode => RadioMode.Am;

    public ModeAnalysis Analyze(in ModeAnalysisContext context)
    {
        double score = AnalyzerHelpers.EnvelopeScore(Mode, context.Peak);
        score += AnalyzerHelpers.HintBoost(context, Mode);
        if (context.Peak.CarrierProminenceDb > 10 && Math.Abs(context.Peak.Asymmetry) < 0.35)
            score += 0.22;
        string reason = context.Peak.CarrierProminenceDb > 10
            ? "AM-like carrier and envelope width."
            : "Bandwidth in AM range; carrier not prominent.";
        return AnalyzerHelpers.Result(Mode, score, context, reason);
    }
}

public sealed class NbfmAnalyzer : IModeAnalyzer
{
    public string Id => "nbfm";
    public RadioMode Mode => RadioMode.Nbfm;

    public ModeAnalysis Analyze(in ModeAnalysisContext context)
    {
        double score = AnalyzerHelpers.EnvelopeScore(Mode, context.Peak);
        score += AnalyzerHelpers.HintBoost(context, Mode);
        if (context.Peak.CarrierProminenceDb <= 10 && context.Peak.BandwidthHz is > 4_500 and <= 20_000)
            score += 0.18;
        double offset = AnalyzerHelpers.OffsetHz(context);
        if (DmrSync.Present(context.Iq.Span, context.SampleRate, offset)
            || DstarSync.Present(context.Iq.Span, context.SampleRate, offset))
            score = Math.Min(score, 0.25);
        return AnalyzerHelpers.Result(Mode, score, context,
            score >= 0.45 ? "Narrow-FM envelope without a strong AM carrier." : "Weak fit to NBFM.");
    }
}

public sealed class WbfmAnalyzer : IModeAnalyzer
{
    public string Id => "wbfm";
    public RadioMode Mode => RadioMode.Wbfm;

    public ModeAnalysis Analyze(in ModeAnalysisContext context)
    {
        double score = AnalyzerHelpers.EnvelopeScore(Mode, context.Peak);
        score += AnalyzerHelpers.HintBoost(context, Mode);
        if (context.Peak.BandwidthHz is >= 30_000 and <= 300_000)
            score += 0.20;

        DemodResult? demod = AnalyzerHelpers.TryDemod(context, Mode);
        if (demod?.Text is { Length: > 0 } text
            && text.Contains("RDS", StringComparison.OrdinalIgnoreCase))
        {
            score = Math.Max(score, 0.85);
            return AnalyzerHelpers.Result(Mode, score, context, "Broadcast-FM width with RDS text.", true);
        }

        return AnalyzerHelpers.Result(Mode, score, context,
            score >= 0.45 ? "Wide FM broadcast envelope." : "Weak fit to WBFM.");
    }
}

file static class SidebandLogic
{
    public static ModeAnalysis SidebandAnalyze(RadioMode mode, in ModeAnalysisContext context, bool preferPositiveAsymmetry)
    {
        double score = AnalyzerHelpers.EnvelopeScore(mode, context.Peak);
        score += AnalyzerHelpers.HintBoost(context, mode);
        if (context.Peak.BandwidthHz is >= 1_400 and <= 4_500)
        {
            score += 0.12;
            if (preferPositiveAsymmetry && context.Peak.Asymmetry > 0.3)
                score += 0.18;
            else if (!preferPositiveAsymmetry && context.Peak.Asymmetry < -0.3)
                score += 0.18;
            else if (Math.Abs(context.Peak.Asymmetry) < 0.3)
                score += 0.04;
        }

        if (context.Peak.CarrierProminenceDb > 10 && Math.Abs(context.Peak.Asymmetry) < 0.35)
            score = Math.Min(score, 0.28);

        string side = mode == RadioMode.Usb ? "USB" : "LSB";
        string reason = Math.Abs(context.Peak.Asymmetry) > 0.3
            ? $"{side} envelope with matching sideband asymmetry."
            : $"{side} bandwidth; asymmetry inconclusive.";
        return AnalyzerHelpers.Result(mode, score, context, reason);
    }
}
