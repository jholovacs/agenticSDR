namespace AgenticSdr.Processors;

public sealed class RdsProperties
{
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Emits RDS lines carried on the frame as meta key "rdsLines" (string[] or IEnumerable&lt;string&gt;).
/// The Host attaches lines from the WBFM mux before submitting.
/// </summary>
public sealed class RdsProcessor : ISignalProcessor
{
    public const string ProcessorId = "rds";
    private readonly RdsProperties _properties;

    public RdsProcessor(RdsProperties? properties = null) =>
        _properties = properties ?? new RdsProperties();

    public string Id => ProcessorId;
    public string DisplayName => "RDS";
    public ProcessorKind Kind => ProcessorKind.Decoder;
    public object Properties => _properties;

    public SignalProcessResult Process(SignalFrame input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_properties.Enabled)
            return SignalProcessResult.Pass(input);

        if (!input.Meta.TryGetValue("rdsLines", out object? raw) || raw is null)
            return SignalProcessResult.Pass(input);

        var texts = new List<AttributedText>();
        IEnumerable<string> lines = raw switch
        {
            string[] arr => arr,
            IEnumerable<string> seq => seq,
            string one => [one],
            _ => []
        };
        foreach (string line in lines)
        {
            if (!string.IsNullOrWhiteSpace(line))
                texts.Add(new AttributedText(Id, DisplayName, "rds", line.Trim()));
        }

        input.Meta.TryRemove("rdsLines", out _);
        return new SignalProcessResult(input, texts);
    }
}
