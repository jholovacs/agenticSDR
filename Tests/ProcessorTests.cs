using System.Diagnostics;
using AgenticSdr.Dsp;
using AgenticSdr.Processors;

namespace AgenticSdr.Tests;

public class ProcessorTests
{
    [Fact]
    public void ModePipelines_usb_includes_morse_then_speech()
    {
        Assert.Equal(["morse", "speech"], ModePipelines.DefaultIds(RadioMode.Usb));
        Assert.Equal(["morse"], ModePipelines.DefaultIds(RadioMode.Cw));
        Assert.Equal(["ft8"], ModePipelines.DefaultIds(RadioMode.Ft8));
        Assert.Equal(["packet"], ModePipelines.DefaultIds(RadioMode.Packet));
        Assert.Equal(["ribbit"], ModePipelines.DefaultIds(RadioMode.Ribbit));
    }

    [Fact]
    public void MorseProcessor_decodes_clean_cw()
    {
        float[] audio = Key("CQ TEST", wpm: 20);
        var processor = new MorseProcessor();
        SignalProcessResult result = processor.Process(SignalFrame.FromAudio(audio, 48_000), CancellationToken.None);
        Assert.Contains("CQ", result.Texts.Single().Text);
        Assert.Equal("morse", result.Texts.Single().Kind);
        Assert.Equal("Morse", result.Texts.Single().SourceName);
    }

    [Fact]
    public async Task SignalChain_attributes_text_and_runs_off_submit_thread()
    {
        float[] audio = Key("CQ TEST", wpm: 20);
        using var chain = new SignalChain([new MorseProcessor()]);
        Assert.True(chain.TrySubmit(SignalFrame.FromAudio(audio, 48_000)));
        await chain.WaitIdleAsync(CancellationToken.None, settleMs: 30);

        List<AttributedText> texts = [];
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            texts.AddRange(chain.DrainTexts());
            if (texts.Any(t => t.Text.Contains("CQ", StringComparison.Ordinal) && t.SourceId == "morse"))
                break;
            await Task.Delay(20);
        }

        Assert.Contains(texts, t => t.Text.Contains("CQ", StringComparison.Ordinal) && t.SourceId == "morse");
    }

    [Fact]
    public async Task SignalChain_trySubmit_does_not_block_on_slow_stage()
    {
        using var chain = new SignalChain([new SlowProcessor(TimeSpan.FromMilliseconds(400))]);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 8; i++)
            Assert.True(chain.TrySubmit(SignalFrame.FromAudio(new float[1000], 48_000)));
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 200, $"TrySubmit blocked for {sw.ElapsedMilliseconds} ms");
        await Task.Delay(50);
    }

    [Fact]
    public void Catalog_creates_known_processors()
    {
        var catalog = new ProcessorCatalog(new FakeSpeech());
        Assert.Contains(catalog.List(), p => p.Id == "speech");
        using SignalChain? chain = catalog.TryCreateDefault(RadioMode.Nbfm);
        Assert.NotNull(chain);
        Assert.Equal("speech", chain!.Stages[0].Id);
    }

    private sealed class SlowProcessor(TimeSpan delay) : ISignalProcessor
    {
        public string Id => "slow";
        public string DisplayName => "Slow";
        public ProcessorKind Kind => ProcessorKind.Filter;
        public object Properties { get; } = new();

        public SignalProcessResult Process(SignalFrame input, CancellationToken cancellationToken)
        {
            Thread.Sleep(delay);
            return SignalProcessResult.Pass(input);
        }
    }

    private sealed class FakeSpeech : ISpeechTranscriber
    {
        public Task<string> TranscribeAsync(ReadOnlyMemory<float> audio, int sampleRate, CancellationToken cancellationToken) =>
            Task.FromResult("hello");
    }

    private static float[] Key(string text, int wpm)
    {
        const int sampleRate = 48_000;
        double dit = sampleRate * 1.2 / wpm;
        var samples = new List<float>();
        double phase = 0;
        void Add(bool mark, double units)
        {
            int count = Math.Max(1, (int)Math.Round(dit * units));
            for (int i = 0; i < count; i++)
            {
                phase += 2 * Math.PI * 700 / sampleRate;
                samples.Add(mark ? (float)Math.Sin(phase) : 0);
            }
        }

        foreach (char raw in text)
        {
            if (raw == ' ')
            {
                Add(false, 4);
                continue;
            }

            string pattern = raw switch
            {
                'C' => "-.-.",
                'Q' => "--.-",
                'T' => "-",
                'E' => ".",
                'S' => "...",
                _ => ""
            };
            foreach (char mark in pattern)
            {
                Add(true, mark == '-' ? 3 : 1);
                Add(false, 1);
            }

            Add(false, 2);
        }

        return samples.ToArray();
    }
}
