namespace AgenticSdr.Radio;

/// <summary>
/// Test facade over a receive-only (or real) radio that reports transmit capability and discards I/Q.
/// Nothing is radiated; mode desks use this so Transmit can be exercised without TX hardware.
/// </summary>
public sealed class SimulatedTransmitRadio : IRadioDevice, IRadioTransmitter
{
    public SimulatedTransmitRadio(IRadioDevice inner)
    {
        Inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public IRadioDevice Inner { get; }

    public RadioCapabilities Capabilities => Inner.Capabilities with { CanTransmit = true };

    public bool DirectSampling => Inner.DirectSampling;

    public double CenterFrequencyHz => Inner.CenterFrequencyHz;

    public double SampleRateHz => Inner.SampleRateHz;

    public SampleEncoding Encoding => Inner.Encoding;

    public bool CanTransmit => true;

    public bool Radiates => false;

    public int ReadAlignment => Inner.ReadAlignment;

    public void Tune(double frequencyHz) => Inner.Tune(frequencyHz);

    public void SetGain(double? gainDb) => Inner.SetGain(gainDb);

    public void SetFrequencyCorrection(int ppm) => Inner.SetFrequencyCorrection(ppm);

    public int Read(byte[] buffer) => Inner.Read(buffer);

    public void WriteTransmit(ReadOnlySpan<float> interleavedIq)
    {
        // Intentional void sink for test transmit.
    }

    public void Dispose() => Inner.Dispose();
}
