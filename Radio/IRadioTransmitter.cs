namespace AgenticSdr.Radio;

/// <summary>
/// An open radio that can radiate complex baseband. Mode projects call this only after
/// the shared transmit gate has accepted an authorized operator click, Part 97, and CanTransmit.
/// </summary>
public interface IRadioTransmitter
{
    /// <summary>Write interleaved complex float I/Q at the radio's transmit sample rate.</summary>
    void WriteTransmit(ReadOnlySpan<float> interleavedIq);

    /// <summary>
    /// False when this is a test sink (<see cref="SimulatedTransmitRadio"/>) that discards I/Q.
    /// Real transmitters leave the default true.
    /// </summary>
    bool Radiates => true;
}
