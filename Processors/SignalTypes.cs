using System.Collections.Concurrent;
using System.Numerics;

namespace AgenticSdr.Processors;

/// <summary>Whether a stage reshapes the signal or turns it into text (or both).</summary>
public enum ProcessorKind
{
    Filter,
    Decoder
}

/// <summary>
/// One chunk of digital radio signal. Audio is baseband PCM from the demodulator.
/// Iq is optional complex samples when a stage needs them. Meta carries chain flags between stages.
/// </summary>
public sealed class SignalFrame
{
    public Complex[]? Iq { get; init; }
    public float[] Audio { get; init; } = [];
    public int SampleRate { get; init; }
    public int Channels { get; init; } = 1;
    public ConcurrentDictionary<string, object?> Meta { get; } = new(StringComparer.Ordinal);

    public SignalFrame CloneAudio()
    {
        var copy = new SignalFrame
        {
            Iq = Iq is null ? null : (Complex[])Iq.Clone(),
            Audio = (float[])Audio.Clone(),
            SampleRate = SampleRate,
            Channels = Channels
        };
        foreach (var pair in Meta)
            copy.Meta[pair.Key] = pair.Value;
        return copy;
    }

    public SignalFrame WithAudio(float[] audio, int sampleRate, int channels = 1)
    {
        var copy = new SignalFrame
        {
            Iq = Iq,
            Audio = audio,
            SampleRate = sampleRate,
            Channels = channels
        };
        foreach (var pair in Meta)
            copy.Meta[pair.Key] = pair.Value;
        return copy;
    }

    public static SignalFrame FromAudio(float[] audio, int sampleRate, int channels = 1) =>
        new() { Audio = audio, SampleRate = sampleRate, Channels = channels };
}

/// <summary>Decoded text tagged with the processor that produced it.</summary>
public sealed record AttributedText(string SourceId, string SourceName, string Kind, string Text);

/// <summary>What a stage hands to the next stage, plus any text it decoded.</summary>
public sealed record SignalProcessResult(SignalFrame Frame, IReadOnlyList<AttributedText> Texts)
{
    public static SignalProcessResult Pass(SignalFrame frame) => new(frame, []);

    public static SignalProcessResult Text(SignalFrame frame, AttributedText text) =>
        new(frame, [text]);
}

/// <summary>Public description of one stage for status and the page.</summary>
public sealed record PipelineStageInfo(string Id, string Name, string Kind);

/// <summary>Catalog entry returned by GET /api/processors.</summary>
public sealed record ProcessorInfo(string Id, string Name, string Kind);
