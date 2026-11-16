using AgenticSdr.Dsp;

namespace AgenticSdr.Processors;

public sealed class DigiProperties
{
    /// <summary>Unused placeholder for future slot/tone overrides; keeps the Properties pattern uniform.</summary>
    public string Variant { get; set; } = "";
}

public abstract class DigiProcessorBase : ISignalProcessor
{
    private readonly DigiProperties _properties;
    private readonly Func<ReadOnlySpan<float>, int, string> _decode;

    protected DigiProcessorBase(
        string id,
        string displayName,
        string kind,
        Func<ReadOnlySpan<float>, int, string> decode,
        DigiProperties? properties = null)
    {
        Id = id;
        DisplayName = displayName;
        KindKey = kind;
        _decode = decode;
        _properties = properties ?? new DigiProperties();
    }

    public string Id { get; }
    public string DisplayName { get; }
    public string KindKey { get; }
    public ProcessorKind Kind => ProcessorKind.Decoder;
    public object Properties => _properties;

    public SignalProcessResult Process(SignalFrame input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (input.Audio.Length < input.SampleRate)
            return SignalProcessResult.Pass(input);

        string text = _decode(input.Audio, input.SampleRate);
        if (string.IsNullOrWhiteSpace(text))
            return SignalProcessResult.Pass(input);

        return SignalProcessResult.Text(
            input,
            new AttributedText(Id, DisplayName, KindKey, text.Trim()));
    }
}

public sealed class Ft8Processor() : DigiProcessorBase(
    "ft8", "FT8", "ft8",
    (audio, rate) => Ft8Decoder.Format(Ft8Decoder.Decode(audio, rate)));

public sealed class Js8Processor() : DigiProcessorBase(
    "js8", "JS8", "js8",
    (audio, rate) => Js8Decoder.Format(Js8Decoder.Decode(audio, rate)));

public sealed class OliviaProcessor() : DigiProcessorBase(
    "olivia", "Olivia", "olivia",
    (audio, rate) => OliviaDecoder.Format(OliviaDecoder.Decode(audio, rate)));

public sealed class JttyProcessor() : DigiProcessorBase(
    "jtty", "JTTY", "jtty",
    (audio, rate) => JttyDecoder.Format(JttyDecoder.Decode(audio, rate)));

public sealed class VaracProcessor() : DigiProcessorBase(
    "varac", "VarAC", "varac",
    XaraMonitor.FormatAudio);

public sealed class PacketProcessor() : DigiProcessorBase(
    "packet", "Packet", "packet",
    PacketDecoder.Decode);
