using System.Threading.Channels;

namespace AgenticSdr.Host;

/// <summary>
/// One dedicated decode thread. The live audio loop only enqueues sample copies (DropOldest)
/// and drains completed text — never runs Walsh/AFSK/VarAC work on the radio pump.
/// </summary>
internal sealed class LiveDecodeWorker : IDisposable
{
    private readonly Channel<float[]> _inbox;
    private readonly Channel<(string Kind, string Text)> _outbox;
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;
    private bool _disposed;

    public LiveDecodeWorker(string name, Action<float[], CancellationToken, Action<string, string>> process)
    {
        _inbox = Channel.CreateBounded<float[]>(new BoundedChannelOptions(2)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });
        _outbox = Channel.CreateUnbounded<(string, string)>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

        _thread = new Thread(() =>
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    float[] block;
                    try
                    {
                        if (!_inbox.Reader.WaitToReadAsync(_cts.Token).AsTask().GetAwaiter().GetResult())
                            break;
                        if (!_inbox.Reader.TryRead(out block!))
                            continue;
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    try
                    {
                        process(block, _cts.Token, (kind, text) =>
                        {
                            if (!string.IsNullOrWhiteSpace(text))
                                _outbox.Writer.TryWrite((kind, text));
                        });
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception)
                    {
                        // Keep listening; a bad block must not kill the decode thread.
                    }
                }
            }
            finally
            {
                _outbox.Writer.TryComplete();
            }
        })
        {
            IsBackground = true,
            Name = name
        };
        _thread.Start();
    }

    /// <summary>Non-blocking. Copies samples so the audio pump can reuse its buffer.</summary>
    public bool TryEnqueue(ReadOnlySpan<float> samples)
    {
        if (_disposed || samples.Length == 0)
            return false;
        var copy = new float[samples.Length];
        samples.CopyTo(copy);
        return _inbox.Writer.TryWrite(copy);
    }

    public void Drain(Action<string, string> onText)
    {
        while (_outbox.Reader.TryRead(out (string Kind, string Text) item))
            onText(item.Kind, item.Text);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _cts.Cancel();
        _inbox.Writer.TryComplete();
        if (!_thread.Join(TimeSpan.FromSeconds(2)))
        {
            // Abandon if stuck in native decode.
        }

        _cts.Dispose();
    }
}
