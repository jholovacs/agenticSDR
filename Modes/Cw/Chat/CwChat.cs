namespace AgenticSdr.Cw.Chat;

public sealed record ChatMessage(string Id, string Direction, string Text, DateTimeOffset At);

/// <summary>RX/TX Morse chat log for the CW desk.</summary>
public sealed class CwChat
{
    readonly object _gate = new();
    readonly List<ChatMessage> _messages = [];
    string _rxShown = "";
    string _rxWindow = "";

    public IReadOnlyList<ChatMessage> Snapshot()
    {
        lock (_gate)
            return _messages.ToArray();
    }

    public void AddTx(string text)
    {
        string clean = Normalize(text);
        if (clean.Length == 0)
            return;
        lock (_gate)
        {
            SealRxUnlocked();
            _messages.Add(new ChatMessage(Guid.NewGuid().ToString("n"), "tx", clean, DateTimeOffset.UtcNow));
            TrimUnlocked();
        }
    }

    public void AddNote(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        lock (_gate)
        {
            _messages.Add(new ChatMessage(Guid.NewGuid().ToString("n"), "note", text.Trim(), DateTimeOffset.UtcNow));
            TrimUnlocked();
        }
    }

    /// <summary>Update the live RX Morse line as the decoder re-reads a moving audio window.</summary>
    public void UpdateRx(string window)
    {
        string next = Normalize(window);
        if (next.Length == 0)
            return;

        lock (_gate)
        {
            string grown = Grow(_rxShown, _rxWindow, next);
            _rxWindow = next;
            if (grown == _rxShown)
                return;
            _rxShown = grown;

            if (_messages.Count > 0 && _messages[^1].Direction == "rx")
                _messages[^1] = _messages[^1] with { Text = grown, At = DateTimeOffset.UtcNow };
            else
                _messages.Add(new ChatMessage(Guid.NewGuid().ToString("n"), "rx", grown, DateTimeOffset.UtcNow));
            TrimUnlocked();
        }
    }

    public void SealRx()
    {
        lock (_gate)
            SealRxUnlocked();
    }

    static string Normalize(string text) =>
        string.IsNullOrWhiteSpace(text) ? "" : text.Trim().ToUpperInvariant();

    void SealRxUnlocked()
    {
        _rxShown = "";
        _rxWindow = "";
    }

    void TrimUnlocked()
    {
        while (_messages.Count > 200)
            _messages.RemoveAt(0);
    }

    /// <summary>Same growth rule Host uses for live Morse: keep letters that scrolled out of the window.</summary>
    public static string Grow(string shown, string previousWindow, string next)
    {
        if (string.IsNullOrEmpty(next))
            return shown;
        if (previousWindow.Length == 0)
            return string.IsNullOrEmpty(shown) ? next : shown + " " + next;
        if (next.StartsWith(previousWindow, StringComparison.Ordinal))
            return shown + next[previousWindow.Length..];
        if (previousWindow.StartsWith(next, StringComparison.Ordinal))
            return shown;

        int overlap = SharedTail(previousWindow, next);
        if (overlap > 0)
            return shown + next[overlap..];
        return shown.Length == 0 ? next : shown + " " + next;
    }

    static int SharedTail(string previous, string next)
    {
        int max = Math.Min(previous.Length, next.Length);
        for (int length = max; length > 0; length--)
        {
            if (previous.AsSpan(previous.Length - length).SequenceEqual(next.AsSpan(0, length)))
                return length;
        }

        return 0;
    }
}
