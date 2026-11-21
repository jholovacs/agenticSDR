using System.Numerics;
using AgenticSdr.Dsp;

namespace AgenticSdr.Processors;

/// <summary>
/// One-shot mode probe used by Analyze. Frequency and a short IQ capture go in;
/// a confidence score (and optional decode confirmation) come out for ranking.
/// </summary>
public interface IModeAnalyzer
{
    string Id { get; }
    RadioMode Mode { get; }
    ModeAnalysis Analyze(in ModeAnalysisContext context);
}

/// <summary>IQ + spectrum peak for a single Analyze pass.</summary>
public readonly record struct ModeAnalysisContext(
    double FrequencyHz,
    SpectralPeak Peak,
    ReadOnlyMemory<Complex> Iq,
    double CenterHz,
    double SampleRate,
    RadioMode? BandHintMode = null,
    double? PublishedDialHz = null);

/// <summary>Ranked guess for one mode. Confidence is 0..1.</summary>
public sealed record ModeAnalysis(
    RadioMode Mode,
    double Confidence,
    double TuneHz,
    double BandwidthHz,
    string Reason,
    bool DecodeConfirmed);
