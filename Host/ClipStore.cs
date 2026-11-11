namespace AgenticSdr.Host;

/// <summary>One listen recording: the WAV bytes, any decoded Morse or FT8 text, and where it was tuned.</summary>
public sealed class AudioClip
{
    public required string Id { get; init; }
    public required byte[] Wav { get; init; }
    public string? Morse { get; init; }
    public double FrequencyHz { get; init; }
    public string Mode { get; init; } = "";
}

/// <summary>Keeps the most recent listen clips so the page and the AI can fetch the audio after the capture has finished.</summary>
public sealed class ClipStore
{
    private readonly object _gate = new();
    private readonly LinkedList<AudioClip> _order = [];
    private readonly Dictionary<string, LinkedListNode<AudioClip>> _index = new(StringComparer.Ordinal);

    public AudioClip Add(byte[] wav, string? morse, double frequencyHz, string mode)
    {
        var clip = new AudioClip
        {
            Id = Guid.NewGuid().ToString("N"),
            Wav = wav,
            Morse = morse,
            FrequencyHz = frequencyHz,
            Mode = mode
        };

        lock (_gate)
        {
            LinkedListNode<AudioClip> node = _order.AddLast(clip);
            _index[clip.Id] = node;
            while (_order.Count > 12 && _order.First is not null)
            {
                _index.Remove(_order.First.Value.Id);
                _order.RemoveFirst();
            }
        }

        return clip;
    }

    public bool TryGet(string id, out AudioClip? clip)
    {
        lock (_gate)
        {
            if (_index.TryGetValue(id, out LinkedListNode<AudioClip>? node))
            {
                clip = node.Value;
                return true;
            }
        }

        clip = null;
        return false;
    }

    /// <summary>Most recent listen clip, or null when nothing has been captured yet.</summary>
    public AudioClip? Latest()
    {
        lock (_gate)
            return _order.Last?.Value;
    }

    public IReadOnlyList<AudioClip> Recent(int max = 8)
    {
        lock (_gate)
        {
            max = Math.Clamp(max, 1, 12);
            var list = new List<AudioClip>(Math.Min(max, _order.Count));
            for (LinkedListNode<AudioClip>? node = _order.Last; node is not null && list.Count < max; node = node.Previous)
                list.Add(node.Value);
            return list;
        }
    }
}
