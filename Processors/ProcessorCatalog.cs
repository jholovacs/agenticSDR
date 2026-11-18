using AgenticSdr.Dsp;

namespace AgenticSdr.Processors;

/// <summary>Builds processor instances and describes the catalog for the API.</summary>
public sealed class ProcessorCatalog(ISpeechTranscriber? speech = null)
{
    private readonly ISpeechTranscriber? _speech = speech;

    public IReadOnlyList<ProcessorInfo> List() =>
    [
        new(MorseProcessor.ProcessorId, "Morse", "decoder"),
        new(SpeechProcessor.ProcessorId, "Speech", "decoder"),
        new("ft8", "FT8", "decoder"),
        new("js8", "JS8", "decoder"),
        new("olivia", "Olivia", "decoder"),
        new("jtty", "JTTY", "decoder"),
        new("wspr", "WSPR", "decoder"),
        new("varac", "VarAC", "decoder"),
        new("packet", "Packet", "decoder"),
        new("ribbit", "Ribbit", "decoder"),
        new(RdsProcessor.ProcessorId, "RDS", "decoder")
    ];

    public ISignalProcessor Create(string id, RadioMode mode = RadioMode.Unknown)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return id.Trim().ToLowerInvariant() switch
        {
            MorseProcessor.ProcessorId => new MorseProcessor(new MorseProperties
            {
                Audible = mode is RadioMode.Usb or RadioMode.Lsb
            }),
            SpeechProcessor.ProcessorId => CreateSpeech(mode),
            "ft8" => new Ft8Processor(),
            "js8" => new Js8Processor(),
            "olivia" => new OliviaProcessor(),
            "jtty" => new JttyProcessor(),
            "wspr" => new WsprProcessor(),
            "varac" => new VaracProcessor(),
            "packet" => new PacketProcessor(),
            "ribbit" => new RibbitProcessor(),
            RdsProcessor.ProcessorId => new RdsProcessor(),
            _ => throw new ArgumentException($"Unknown processor '{id}'.")
        };
    }

    public SignalChain CreateChain(IEnumerable<string> ids, RadioMode mode)
    {
        var stages = ids
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => Create(id, mode))
            .ToList();
        if (stages.Count == 0)
            throw new ArgumentException("Pipeline has no processors.");
        return new SignalChain(stages);
    }

    public SignalChain? TryCreateDefault(RadioMode mode)
    {
        IReadOnlyList<string> ids = ModePipelines.DefaultIds(mode);
        if (ids.Count == 0)
            return null;
        // Speech without a transcriber is omitted from the default chain.
        var usable = ids
            .Where(id => id != SpeechProcessor.ProcessorId || _speech is not null)
            .ToList();
        return usable.Count == 0 ? null : CreateChain(usable, mode);
    }

    private SpeechProcessor CreateSpeech(RadioMode mode)
    {
        if (_speech is null)
            throw new InvalidOperationException("Speech processor needs an ISpeechTranscriber.");
        return new SpeechProcessor(_speech, new SpeechProperties
        {
            DeferToMorse = mode is RadioMode.Usb or RadioMode.Lsb
        });
    }
}
