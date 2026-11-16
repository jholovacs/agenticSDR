using AgenticSdr.Dsp;

namespace AgenticSdr.Processors;

public sealed class MorseProperties
{
    /// <summary>When true, use the sideband-audible path (USB/LSB). CW uses the full CW path.</summary>
    public bool Audible { get; set; }
}

public sealed class MorseProcessor : ISignalProcessor
{
    public const string ProcessorId = "morse";
    private readonly MorseProperties _properties;

    public MorseProcessor(MorseProperties? properties = null) =>
        _properties = properties ?? new MorseProperties();

    public string Id => ProcessorId;
    public string DisplayName => "Morse";
    public ProcessorKind Kind => ProcessorKind.Decoder;
    public object Properties => _properties;

    public SignalProcessResult Process(SignalFrame input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (input.Audio.Length < input.SampleRate / 4)
            return SignalProcessResult.Pass(input);

        float[] mono = Mono(input);
        string text = _properties.Audible
            ? MorseDecoder.DecodeAudible(mono, input.SampleRate)
            : MorseDecoder.DecodeCw(mono, input.SampleRate);

        if (!MorseDecoder.IsCopy(text))
        {
            input.Meta["morseCopy"] = false;
            return SignalProcessResult.Pass(input);
        }

        input.Meta["morseCopy"] = true;
        return SignalProcessResult.Text(
            input,
            new AttributedText(Id, DisplayName, "morse", text.Trim()));
    }

    private static float[] Mono(SignalFrame input)
    {
        if (input.Channels <= 1)
            return input.Audio;
        int frames = input.Audio.Length / input.Channels;
        var mono = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            double sum = 0;
            for (int c = 0; c < input.Channels; c++)
                sum += input.Audio[i * input.Channels + c];
            mono[i] = (float)(sum / input.Channels);
        }

        return mono;
    }
}
