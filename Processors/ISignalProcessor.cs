namespace AgenticSdr.Processors;

/// <summary>
/// One filter or decoder in a listen pipeline. Process always runs on that stage's worker thread.
/// Properties is a stage-specific settings object.
/// </summary>
public interface ISignalProcessor
{
    string Id { get; }
    string DisplayName { get; }
    ProcessorKind Kind { get; }
    object Properties { get; }
    SignalProcessResult Process(SignalFrame input, CancellationToken cancellationToken);
}

/// <summary>Optional async path for stages that await I/O (speech model). Awaited on the stage worker thread.</summary>
public interface IAsyncSignalProcessor : ISignalProcessor
{
    Task<SignalProcessResult> ProcessAsync(SignalFrame input, CancellationToken cancellationToken);
}

/// <summary>Host-provided speech engine so Processors does not reference Whisper.</summary>
public interface ISpeechTranscriber
{
    Task<string> TranscribeAsync(ReadOnlyMemory<float> audio, int sampleRate, CancellationToken cancellationToken);
}
