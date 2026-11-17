namespace AgenticSdr.Cw.Listen;

/// <summary>
/// One live PCM sink for the open page — same pattern as Host <c>/api/live</c>:
/// each demod block is written and flushed straight to the HTTP body. No channel
/// queue that can stall USB reads and click.
/// </summary>
public sealed class CwAudioSink
{
    readonly object _gate = new();
    Stream? _output;
    TaskCompletionSource? _ended;
    bool _done;

    public bool Attach(Stream output, out Task ended)
    {
        ArgumentNullException.ThrowIfNull(output);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_done)
            {
                tcs.TrySetResult();
                ended = tcs.Task;
                return false;
            }

            _output = output;
            _ended = tcs;
            ended = tcs.Task;
            return true;
        }
    }

    public void Detach(Stream output)
    {
        TaskCompletionSource? ended = null;
        lock (_gate)
        {
            if (!ReferenceEquals(_output, output))
                return;
            _output = null;
            ended = _ended;
            _ended = null;
        }

        ended?.TrySetResult();
    }

    public async Task WriteAsync(byte[] pcm, CancellationToken cancellationToken)
    {
        Stream? output;
        lock (_gate)
        {
            if (_done)
                return;
            output = _output;
        }

        if (output is null)
            return;

        try
        {
            await output.WriteAsync(pcm, cancellationToken);
            await output.FlushAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            TaskCompletionSource? ended = null;
            lock (_gate)
            {
                if (!ReferenceEquals(_output, output))
                    return;
                _output = null;
                ended = _ended;
                _ended = null;
            }

            ended?.TrySetResult();
        }
    }

    public void Complete()
    {
        TaskCompletionSource? ended;
        lock (_gate)
        {
            _done = true;
            _output = null;
            ended = _ended;
            _ended = null;
        }

        ended?.TrySetResult();
    }
}
