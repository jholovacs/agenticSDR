using System.Numerics;
using AgenticSdr.Dsp;

namespace AgenticSdr.Processors;

internal static class AnalyzerHelpers
{
    public static double OffsetHz(in ModeAnalysisContext ctx) =>
        ctx.FrequencyHz - ctx.CenterHz;

    public static double TuneHz(in ModeAnalysisContext ctx, RadioMode mode)
    {
        if (ctx.PublishedDialHz is double dial && dial > 0)
            return dial;
        return ModeClassifier.CarrierFrequency(ctx.Peak, mode);
    }

    public static double BandwidthHz(RadioMode mode, SpectralPeak peak)
    {
        double nominal = ModeBandwidth.NominalWidthHz(mode);
        if (nominal <= 0)
            return peak.BandwidthHz;
        // Prefer measured width when it is in family; else nominal channel width.
        return ModeBandwidth.FitsWithMargin(mode, peak.BandwidthHz)
            ? peak.BandwidthHz
            : nominal;
    }

    public static double EnvelopeScore(RadioMode mode, SpectralPeak peak)
    {
        if (!ModeBandwidth.FitsWithMargin(mode, peak.BandwidthHz))
            return 0.05;
        PassBandDistance(mode, peak.BandwidthHz, out double closeness);
        double snr = Math.Clamp((peak.SnrDb + 6) / 24.0, 0, 1);
        return 0.30 + 0.20 * closeness + 0.10 * snr;
    }

    public static void PassBandDistance(RadioMode mode, double bandwidthHz, out double closeness)
    {
        ModeBandwidth.Pass? pass = ModeBandwidth.For(mode);
        if (pass is null)
        {
            closeness = 0.5;
            return;
        }

        double center = 0.5 * (pass.Value.MinHz + pass.Value.MaxHz);
        double half = 0.5 * (pass.Value.MaxHz - pass.Value.MinHz);
        if (half < 1)
            half = 1;
        closeness = Math.Clamp(1.0 - Math.Abs(bandwidthHz - center) / (half * 1.5), 0, 1);
    }

    public static double HintBoost(in ModeAnalysisContext ctx, RadioMode mode) =>
        ctx.BandHintMode == mode ? 0.08 : 0;

    public static DemodResult? TryDemod(in ModeAnalysisContext ctx, RadioMode mode)
    {
        try
        {
            return Demodulator.Demodulate(
                ctx.Iq.Span,
                mode,
                OffsetHz(ctx),
                normalize: true,
                inputRate: ctx.SampleRate);
        }
        catch
        {
            return null;
        }
    }

    public static float[] Mono(DemodResult demod)
    {
        if (demod.Channels <= 1)
            return demod.Audio;
        int frames = demod.Audio.Length / demod.Channels;
        var mono = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            double sum = 0;
            for (int c = 0; c < demod.Channels; c++)
                sum += demod.Audio[i * demod.Channels + c];
            mono[i] = (float)(sum / demod.Channels);
        }

        return mono;
    }

    public static ModeAnalysis Result(
        RadioMode mode,
        double confidence,
        in ModeAnalysisContext ctx,
        string reason,
        bool decodeConfirmed = false) =>
        new(
            mode,
            Math.Clamp(confidence, 0, 0.98),
            TuneHz(ctx, mode),
            BandwidthHz(mode, ctx.Peak),
            reason,
            decodeConfirmed);
}
