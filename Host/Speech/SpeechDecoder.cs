using System.Text;
using AgenticSdr.Dsp;
using Whisper.net;
using Whisper.net.Ggml;

namespace AgenticSdr.Host;

/// <summary>
/// Turns spoken audio into text with a small English speech model.
/// The model is downloaded once into models/ggml-tiny.en.bin and is not part of the program.
/// Morse and FT8 keep their own decoders. This one is for AM, narrow FM, broadcast FM, and sideband.
/// </summary>
public sealed class SpeechDecoder : IAsyncDisposable
{
    public const int ModelRate = 16_000;
    private readonly string _modelPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;

    public SpeechDecoder(string contentRoot)
    {
        _modelPath = Path.Combine(contentRoot, "models", "ggml-tiny.en.bin");
    }

    public static bool IsVoice(RadioMode mode) =>
        mode is RadioMode.Nbfm or RadioMode.Wbfm or RadioMode.Am or RadioMode.Usb or RadioMode.Lsb;

    public async Task<string> TranscribeAsync(ReadOnlyMemory<float> audio, int sampleRate, CancellationToken cancellationToken)
    {
        if (sampleRate < 8_000 || audio.Length < sampleRate || !LoudEnough(audio.Span))
            return "";

        await _gate.WaitAsync(cancellationToken);
        try
        {
            WhisperProcessor processor = await OpenAsync(cancellationToken);
            float[] samples = Resample(Normalize(audio.Span), sampleRate, ModelRate);
            var text = new StringBuilder();
            await foreach (SegmentData segment in processor.ProcessAsync(samples, cancellationToken))
            {
                string line = segment.Text.Trim();
                if (line.Length > 0)
                    text.Append(line).Append(' ');
            }

            return text.ToString().Trim();
        }
        finally
        {
            _gate.Release();
        }
    }

    public static float[] Resample(ReadOnlySpan<float> audio, int fromRate, int toRate)
    {
        if (audio.Length == 0 || fromRate <= 0 || toRate <= 0)
            return [];
        if (fromRate == toRate)
            return audio.ToArray();

        int count = (int)((long)audio.Length * toRate / fromRate);
        var output = new float[count];
        double step = (double)fromRate / toRate;
        for (int i = 0; i < count; i++)
        {
            double at = i * step;
            int left = Math.Min((int)at, audio.Length - 1);
            int right = Math.Min(left + 1, audio.Length - 1);
            float fraction = (float)(at - left);
            output[i] = audio[left] * (1 - fraction) + audio[right] * fraction;
        }

        return output;
    }

    private async Task<WhisperProcessor> OpenAsync(CancellationToken cancellationToken)
    {
        if (_processor is not null)
            return _processor;

        if (!File.Exists(_modelPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_modelPath)!);
            using Stream downloaded = await WhisperGgmlDownloader.GetGgmlModelAsync(GgmlType.TinyEn, QuantizationType.NoQuantization, cancellationToken);
            string partial = _modelPath + ".partial";
            await using (var file = File.Create(partial))
                await downloaded.CopyToAsync(file, cancellationToken);
            File.Move(partial, _modelPath, overwrite: true);
        }

        _factory = WhisperFactory.FromPath(_modelPath);
        _processor = _factory.CreateBuilder()
            .WithLanguage("en")
            .WithTemperature(0)
            .WithNoSpeechThreshold(0.6f)
            .WithThreads(Math.Max(1, Environment.ProcessorCount / 2))
            .Build();
        return _processor;
    }

    private static float[] Normalize(ReadOnlySpan<float> audio)
    {
        float peak = 0;
        foreach (float sample in audio)
            peak = Math.Max(peak, Math.Abs(sample));
        var scaled = new float[audio.Length];
        float gain = peak < 1e-4f ? 0 : 0.8f / peak;
        for (int i = 0; i < audio.Length; i++)
            scaled[i] = audio[i] * gain;
        return scaled;
    }

    private static bool LoudEnough(ReadOnlySpan<float> audio)
    {
        double energy = 0;
        foreach (float sample in audio)
            energy += sample * sample;
        return audio.Length > 0 && Math.Sqrt(energy / audio.Length) > 0.01;
    }

    public async ValueTask DisposeAsync()
    {
        if (_processor is not null)
            await _processor.DisposeAsync();
        _factory?.Dispose();
    }
}
