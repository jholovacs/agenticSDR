namespace AgenticSdr.Radio;

/// <summary>
/// How one I/Q sample is packed into the bytes from <see cref="IRadioDevice.Read"/>.
/// I and Q are the pair of numbers that describe the radio wave at one moment.
/// </summary>
public enum SampleEncoding
{
    /// <summary>Unsigned 8-bit I and Q. This is what an RTL-SDR dongle produces.</summary>
    Cu8,

    /// <summary>32-bit floating-point I and Q. SoapySDR radios are converted to this before the rest of the program sees them.</summary>
    Cf32
}

/// <summary>
/// One open USB radio. Tune moves the center frequency. Read fills a buffer with samples.
/// The session is the only caller, because a dongle has a single input.
/// </summary>
public interface IRadioDevice : IDisposable
{
    /// <summary>Name, tuning range, sample rate, gain, and whether HF uses direct sampling.</summary>
    RadioCapabilities Capabilities { get; }

    /// <summary>True when the radio is in direct-sampling mode for the current frequency. On an RTL-SDR that means below 24 MHz.</summary>
    bool DirectSampling { get; }

    /// <summary>The frequency in the middle of the slice being captured, in hertz.</summary>
    double CenterFrequencyHz { get; }

    /// <summary>How many I/Q pairs arrive each second.</summary>
    double SampleRateHz { get; }

    /// <summary>The byte layout of <see cref="Read"/>.</summary>
    SampleEncoding Encoding { get; }

    /// <summary>Read sizes must be a multiple of this many bytes. 512 for the RTL-SDR, 8 for SoapySDR complex floats.</summary>
    int ReadAlignment { get; }

    /// <summary>Move the center frequency. The samples already in the radio's buffer are discarded.</summary>
    void Tune(double frequencyHz);

    /// <summary>Set the tuner gain in dB. Null lets the radio choose its own gain. The open-time gain is still the one in <see cref="Capabilities"/>.</summary>
    void SetGain(double? gainDb);

    /// <summary>Block until the buffer is filled, and return how many bytes were written.</summary>
    int Read(byte[] buffer);
}
