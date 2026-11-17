namespace AgenticSdr.Host;

/// <summary>
/// One piece of decoded text. Kind is morse, speech, rds, ft8, packet, or note (CSS).
/// Source is the filter/decoder display name that produced the text.
/// </summary>
public sealed record TranscriptSpan(string Kind, string Text, string? Source = null);

/// <summary>
/// Grows a Morse line as the decoder re-reads a moving window of audio.
/// Letters that have scrolled out of the window stay on the line.
/// </summary>
public static class TranscriptLog
{
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

    private static int SharedTail(string previous, string next)
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
