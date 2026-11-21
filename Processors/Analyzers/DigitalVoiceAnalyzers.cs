using AgenticSdr.Dsp;

namespace AgenticSdr.Processors;

public sealed class DmrAnalyzer : IModeAnalyzer
{
    public string Id => "dmr";
    public RadioMode Mode => RadioMode.Dmr;

    public ModeAnalysis Analyze(in ModeAnalysisContext context)
    {
        double score = AnalyzerHelpers.EnvelopeScore(Mode, context.Peak);
        score += AnalyzerHelpers.HintBoost(context, Mode);
        double offset = AnalyzerHelpers.OffsetHz(context);
        if (DmrSync.Present(context.Iq.Span, context.SampleRate, offset))
        {
            score = Math.Max(score, 0.92);
            var reader = new DmrReader();
            IReadOnlyList<string> lines = reader.Push(context.Iq.Span, context.SampleRate, offset, default);
            string extra = lines.Count > 0 ? $" {lines[0]}" : "";
            return AnalyzerHelpers.Result(Mode, score, context,
                $"DMR framing confirmed.{extra}", true);
        }

        return AnalyzerHelpers.Result(Mode, score, context,
            score >= 0.35 ? "NBFM-width channel; no DMR framing in this buffer." : "Weak fit to DMR.");
    }
}

public sealed class DstarAnalyzer : IModeAnalyzer
{
    public string Id => "dstar";
    public RadioMode Mode => RadioMode.Dstar;

    public ModeAnalysis Analyze(in ModeAnalysisContext context)
    {
        double score = AnalyzerHelpers.EnvelopeScore(Mode, context.Peak);
        score += AnalyzerHelpers.HintBoost(context, Mode);
        double offset = AnalyzerHelpers.OffsetHz(context);
        if (DstarSync.Present(context.Iq.Span, context.SampleRate, offset))
        {
            score = Math.Max(score, 0.92);
            var reader = new DstarReader();
            IReadOnlyList<string> lines = reader.Push(context.Iq.Span, context.SampleRate, offset);
            string extra = lines.Count > 0 ? $" {lines[0]}" : "";
            return AnalyzerHelpers.Result(Mode, score, context,
                $"D-STAR framing confirmed.{extra}", true);
        }

        return AnalyzerHelpers.Result(Mode, score, context,
            score >= 0.35 ? "NBFM-width channel; no D-STAR framing in this buffer." : "Weak fit to D-STAR.");
    }
}

public sealed class M17Analyzer : IModeAnalyzer
{
    public string Id => "m17";
    public RadioMode Mode => RadioMode.M17;

    public ModeAnalysis Analyze(in ModeAnalysisContext context)
    {
        double score = AnalyzerHelpers.EnvelopeScore(Mode, context.Peak);
        score += AnalyzerHelpers.HintBoost(context, Mode);
        double offset = AnalyzerHelpers.OffsetHz(context);
        try
        {
            using var reader = new M17Reader();
            if (!reader.Available)
            {
                return AnalyzerHelpers.Result(Mode, score * 0.5, context,
                    "M17 envelope candidate; m17voice native library not available.");
            }

            bool noted = false;
            foreach (M17Update update in reader.Push(context.Iq.Span, context.SampleRate, offset))
            {
                if (!string.IsNullOrWhiteSpace(update.Note) || update.Pcm8k is { Length: > 0 })
                {
                    noted = true;
                    break;
                }
            }

            if (noted)
            {
                score = Math.Max(score, 0.88);
                return AnalyzerHelpers.Result(Mode, score, context, "M17 framing / Codec2 activity.", true);
            }
        }
        catch
        {
            // Native missing or failed — keep envelope-only score.
        }

        return AnalyzerHelpers.Result(Mode, score, context,
            score >= 0.35 ? "NBFM-width channel; no M17 activity in this buffer." : "Weak fit to M17.");
    }
}
