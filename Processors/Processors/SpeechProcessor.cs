namespace AgenticSdr.Processors;

public sealed class SpeechProperties
{
    /// <summary>When true, skip transcription if an earlier Morse stage marked Morse copy on this frame.</summary>
    public bool DeferToMorse { get; set; } = true;

    /// <summary>Minimum seconds between Whisper runs on this stage (live chain may submit more often for Morse).</summary>
    public double MinIntervalSeconds { get; set; } = 6;
}

public sealed class SpeechProcessor : IAsyncSignalProcessor
{
    public const string ProcessorId = "speech";
    private readonly ISpeechTranscriber _speech;
    private readonly SpeechProperties _properties;
    private DateTime _nextUtc = DateTime.MinValue;

    public SpeechProcessor(ISpeechTranscriber speech, SpeechProperties? properties = null)
    {
        _speech = speech;
        _properties = properties ?? new SpeechProperties();
    }

    public string Id => ProcessorId;
    public string DisplayName => "Speech";
    public ProcessorKind Kind => ProcessorKind.Decoder;
    public object Properties => _properties;

    public SignalProcessResult Process(SignalFrame input, CancellationToken cancellationToken) =>
        ProcessAsync(input, cancellationToken).GetAwaiter().GetResult();

    public async Task<SignalProcessResult> ProcessAsync(SignalFrame input, CancellationToken cancellationToken)
    {
        if (_properties.DeferToMorse
            && input.Meta.TryGetValue("morseCopy", out object? flag)
            && flag is true)
        {
            return SignalProcessResult.Pass(input);
        }

        if (input.Audio.Length < input.SampleRate)
            return SignalProcessResult.Pass(input);

        DateTime now = DateTime.UtcNow;
        if (_properties.MinIntervalSeconds > 0 && now < _nextUtc)
            return SignalProcessResult.Pass(input);
        _nextUtc = now.AddSeconds(Math.Max(0.5, _properties.MinIntervalSeconds));

        float[] mono = Mono(input);
        string said;
        try
        {
            said = await _speech.TranscribeAsync(mono, input.SampleRate, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return SignalProcessResult.Text(
                input,
                new AttributedText(Id, DisplayName, "note", "Speech was not read. " + ex.Message));
        }

        if (string.IsNullOrWhiteSpace(said))
            return SignalProcessResult.Pass(input);

        return SignalProcessResult.Text(
            input,
            new AttributedText(Id, DisplayName, "speech", said.Trim()));
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
