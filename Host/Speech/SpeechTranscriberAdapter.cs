using AgenticSdr.Processors;

namespace AgenticSdr.Host;

/// <summary>Adapts the Whisper-backed SpeechDecoder to the Processors speech interface.</summary>
public sealed class SpeechTranscriberAdapter(SpeechDecoder speech) : ISpeechTranscriber
{
    public Task<string> TranscribeAsync(ReadOnlyMemory<float> audio, int sampleRate, CancellationToken cancellationToken) =>
        speech.TranscribeAsync(audio, sampleRate, cancellationToken);
}
