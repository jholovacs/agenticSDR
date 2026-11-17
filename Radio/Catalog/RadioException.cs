namespace AgenticSdr.Radio;

/// <summary>
/// The radio could not do what was asked: missing library, dongle not opened, frequency out of range, or a read that returned nothing.
/// The message is written for a person, and the web page and the AI tools show it as-is.
/// </summary>
public sealed class RadioException : Exception
{
    public RadioException(string message) : base(message)
    {
    }

    public RadioException(string message, Exception inner) : base(message, inner)
    {
    }
}
