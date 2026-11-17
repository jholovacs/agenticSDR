using System.Threading.Channels;

namespace AgenticSdr.Host;

/// <summary>
/// Hands the same live audio to every open page. A slow page drops chunks instead of stalling the radio.
/// </summary>
public sealed class CampFanout
{
    private readonly object _gate = new();
    private readonly List<Channel<byte[]>> _listeners = [];
    private bool _done;

    public ChannelReader<byte[]> Join()
    {
        var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(8)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });
        lock (_gate)
        {
            if (_done)
            {
                channel.Writer.TryComplete();
                return channel.Reader;
            }

            _listeners.Add(channel);
        }

        return channel.Reader;
    }

    public void Leave(ChannelReader<byte[]> reader)
    {
        lock (_gate)
            _listeners.RemoveAll(channel => channel.Reader == reader);
    }

    public void Publish(byte[] pcm)
    {
        Channel<byte[]>[] copy;
        lock (_gate)
        {
            if (_done)
                return;
            copy = _listeners.ToArray();
        }

        foreach (Channel<byte[]> channel in copy)
            channel.Writer.TryWrite(pcm);
    }

    public void Complete()
    {
        Channel<byte[]>[] copy;
        lock (_gate)
        {
            _done = true;
            copy = _listeners.ToArray();
            _listeners.Clear();
        }

        foreach (Channel<byte[]> channel in copy)
            channel.Writer.TryComplete();
    }
}
