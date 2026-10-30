namespace AgenticSdr.Radio;

/// <summary>
/// What the open radio can do. Gain is null when the radio is choosing its own amplification.
/// Ppm is the clock correction in parts per million. DirectSamplingBelow25MHz is true only for RTL-SDR dongles.
/// </summary>
public sealed record RadioCapabilities(
    string Name,
    string? Serial,
    double MinFrequencyHz,
    double MaxFrequencyHz,
    double SampleRateHz,
    double? GainDb,
    int Ppm,
    bool DirectSamplingBelow25MHz);
