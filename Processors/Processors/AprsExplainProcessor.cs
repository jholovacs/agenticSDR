namespace AgenticSdr.Processors;

/// <summary>
/// Pipeline marker for the APRS plain-English translation that runs with Packet decode.
/// Audio passes through unchanged; the wording is applied when packet text is published.
/// </summary>
public sealed class AprsExplainProcessor : ISignalProcessor
{
    public const string ProcessorId = "aprs-explain";

    public string Id => ProcessorId;
    public string DisplayName => "Plain English";
    public ProcessorKind Kind => ProcessorKind.Filter;
    public object Properties { get; } = new();

    public SignalProcessResult Process(SignalFrame input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return SignalProcessResult.Pass(input);
    }
}
