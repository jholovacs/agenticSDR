namespace AgenticSdr.Radio;

/// <summary>A continuous span of frequencies or sample rates, from minimum to maximum, in hertz.</summary>
public readonly record struct FrequencySpan(double Minimum, double Maximum);

/// <summary>
/// Picks a sample rate the hardware can actually use, preferring 2.4 million samples per second
/// and otherwise the closest rate between 192 kHz and 8 MHz.
/// </summary>
public static class SdrRates
{
    public const double PreferredHz = RadioLimits.DefaultSampleRateHz;
    public const double MinUsefulHz = 192_000;
    public const double MaxUsefulHz = 8_000_000;

    public static double Choose(IReadOnlyList<double> discrete, IReadOnlyList<FrequencySpan> ranges)
    {
        if (discrete.Count > 0)
        {
            IEnumerable<double> useful = discrete.Where(rate => rate >= MinUsefulHz && rate <= MaxUsefulHz);
            IEnumerable<double> pool = useful.Any() ? useful : discrete;
            return pool.MinBy(rate => Math.Abs(rate - PreferredHz));
        }

        if (ranges.Count == 0)
            return PreferredHz;

        if (ranges.Any(span => PreferredHz >= span.Minimum && PreferredHz <= span.Maximum))
            return PreferredHz;

        FrequencySpan nearest = ranges.MinBy(span => Distance(span, PreferredHz));
        double clamped = Math.Clamp(PreferredHz, nearest.Minimum, nearest.Maximum);
        if (clamped > MaxUsefulHz && nearest.Minimum <= MaxUsefulHz)
            return MaxUsefulHz;
        if (clamped < MinUsefulHz && nearest.Maximum >= MinUsefulHz)
            return MinUsefulHz;
        return clamped;
    }

    private static double Distance(FrequencySpan span, double value)
    {
        if (value < span.Minimum)
            return span.Minimum - value;
        if (value > span.Maximum)
            return value - span.Maximum;
        return 0;
    }
}
