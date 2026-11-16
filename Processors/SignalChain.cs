using System.Threading.Channels;

namespace AgenticSdr.Processors;

/// <summary>
/// Ordered filter/decoder pipeline. Each stage has its own dedicated worker thread and a bounded queue.
/// The radio loop only calls <see cref="TrySubmit"/> (non-blocking) and <see cref="DrainTexts"/>.
/// </summary>
public sealed class SignalChain : IDisposable
{
    private const int QueueCapacity = 4;

    private readonly IReadOnlyList<ISignalProcessor> _stages;
    private readonly Channel<SignalFrame>[] _inputs;
    private readonly Channel<AttributedText> _results;
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread[] _workers;
    private readonly PipelineStageInfo[] _info;
    private bool _disposed;

    public SignalChain(IEnumerable<ISignalProcessor> stages)
    {
        _stages = stages.ToArray();
        if (_stages.Count == 0)
            throw new ArgumentException("A signal chain needs at least one stage.", nameof(stages));

        _info = _stages.Select(s => new PipelineStageInfo(s.Id, s.DisplayName, s.Kind.ToString().ToLowerInvariant())).ToArray();
        _inputs = new Channel<SignalFrame>[_stages.Count];
        for (int i = 0; i < _stages.Count; i++)
        {
            _inputs[i] = Channel.CreateBounded<SignalFrame>(new BoundedChannelOptions(QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });
        }

        _results = Channel.CreateUnbounded<AttributedText>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false
        });

        _workers = new Thread[_stages.Count];
        for (int i = 0; i < _stages.Count; i++)
        {
            int index = i;
            var thread = new Thread(() => RunStage(index))
            {
                IsBackground = true,
                Name = $"sdr-processor-{_stages[index].Id}"
            };
            _workers[i] = thread;
            thread.Start();
        }
    }

    public IReadOnlyList<PipelineStageInfo> Stages => _info;

    /// <summary>Non-blocking enqueue for the radio loop. Returns false if the chain is stopped.</summary>
    public bool TrySubmit(SignalFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _inputs[0].Writer.TryWrite(frame.CloneAudio());
    }

    /// <summary>Pull decoded text produced since the last drain. Safe from the radio or status thread.</summary>
    public List<AttributedText> DrainTexts()
    {
        var list = new List<AttributedText>();
        while (_results.Reader.TryRead(out AttributedText? text))
            list.Add(text);
        return list;
    }

    /// <summary>Wait until queued work is idle (tests / clip listen). Not used on the live radio loop.</summary>
    public async Task WaitIdleAsync(CancellationToken cancellationToken, int settleMs = 50)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            bool busy = _inputs.Any(ch => ch.Reader.Count > 0);
            if (!busy)
            {
                await Task.Delay(settleMs, cancellationToken);
                if (_inputs.All(ch => ch.Reader.Count == 0))
                    return;
            }
            else
            {
                await Task.Delay(10, cancellationToken);
            }
        }
    }

    private void RunStage(int index)
    {
        ISignalProcessor processor = _stages[index];
        ChannelReader<SignalFrame> reader = _inputs[index].Reader;
        ChannelWriter<SignalFrame>? next = index + 1 < _inputs.Length ? _inputs[index + 1].Writer : null;
        CancellationToken ct = _cts.Token;

        try
        {
            while (!_cts.IsCancellationRequested)
            {
                SignalFrame frame;
                try
                {
                    if (!reader.WaitToReadAsync(ct).AsTask().GetAwaiter().GetResult())
                        break;
                    if (!reader.TryRead(out frame!))
                        continue;
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                SignalProcessResult result;
                try
                {
                    result = processor is IAsyncSignalProcessor asyncProcessor
                        ? asyncProcessor.ProcessAsync(frame, ct).GetAwaiter().GetResult()
                        : processor.Process(frame, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception)
                {
                    // Keep the chain alive; a bad frame must not kill later stages.
                    result = SignalProcessResult.Pass(frame);
                }

                foreach (AttributedText text in result.Texts)
                {
                    if (!string.IsNullOrWhiteSpace(text.Text))
                        _results.Writer.TryWrite(text);
                }

                if (next is not null)
                    next.TryWrite(result.Frame);
            }
        }
        finally
        {
            // Stage exiting; nothing else to do.
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _cts.Cancel();
        foreach (Channel<SignalFrame> channel in _inputs)
            channel.Writer.TryComplete();
        foreach (Thread worker in _workers)
        {
            if (!worker.Join(TimeSpan.FromSeconds(2)))
            {
                // Background workers; abandon if a stage is stuck in native code.
            }
        }

        _results.Writer.TryComplete();
        _cts.Dispose();
    }
}
