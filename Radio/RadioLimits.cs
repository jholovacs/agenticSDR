namespace AgenticSdr.Radio;

/// <summary>
/// Shared limits for the NESDR path: the frequencies it can tune, the 24 MHz point where direct sampling starts,
/// and the 2.4 million samples per second used so audio can be divided evenly down to 48 kHz.
/// </summary>
public static class RadioLimits
{
    public const double MinFrequencyHz = 100_000;
    public const double MaxFrequencyHz = 1_750_000_000;

    /// <summary>
    /// The Windows librtlsdr build switches to Q-branch direct sampling below 24 MHz
    /// and uses the R820T2 tuner above that. The NESDR SMArt v5 datasheet quotes 25 MHz;
    /// this cutoff matches the driver that actually owns the dongle.
    /// </summary>
    public const double DirectSamplingCutoffHz = 24_000_000;

    /// <summary>
    /// 2.4 MS/s decimates by 50 to 48 kHz audio. Hop step is 1 MHz so each hop's DC spike
    /// falls inside the neighboring hop and is not a blind spot.
    /// </summary>
    public const double DefaultSampleRateHz = 2_400_000;

    public const double HopStepHz = 1_000_000;

    public static double HopStep(double sampleRateHz) =>
        Math.Abs(sampleRateHz - DefaultSampleRateHz) < 1 ? HopStepHz : sampleRateHz * 0.4;

    public static int DirectSamplingModeFor(double frequencyHz)
    {
        if (frequencyHz < MinFrequencyHz || frequencyHz > MaxFrequencyHz)
        {
            throw new RadioException(
                $"Frequency {frequencyHz:0} Hz is outside {MinFrequencyHz:0}–{MaxFrequencyHz:0} Hz.");
        }

        return frequencyHz < DirectSamplingCutoffHz ? 2 : 0;
    }

    public static int AlignBytes(int bytes)
    {
        if (bytes < 512)
            bytes = 512;
        return bytes / 512 * 512;
    }
}
