namespace AgenticSdr.Dsp;

/// <summary>One measured known carrier used to estimate crystal error.</summary>
public sealed record PpmObservation(
    string Name,
    double TrueHz,
    double MeasuredHz,
    double SnrDb,
    double Ppm);

/// <summary>
/// Converts frequency error on published carriers into a crystal correction in parts per million.
/// Positive ppm means the oscillator runs high (peak appears low when the LO is commanded without correction).
/// </summary>
public static class RadioCalibration
{
    /// <summary>How far to hunt around a published carrier when the stick is still uncalibrated (~±100 ppm, floor 40 kHz).</summary>
    public static double SearchHz(double trueHz) =>
        Math.Max(40_000, Math.Abs(trueHz) * 100e-6);

    /// <summary>ppm = (true − measured) / true × 1e6.</summary>
    public static double PpmFromMeasured(double trueHz, double measuredHz)
    {
        if (trueHz <= 0)
            throw new ArgumentOutOfRangeException(nameof(trueHz));
        return (trueHz - measuredHz) / trueHz * 1e6;
    }

    /// <summary>Robust integer ppm from several observations (median, then round).</summary>
    public static int CombinePpm(IReadOnlyList<PpmObservation> observations)
    {
        if (observations is null || observations.Count == 0)
            throw new ArgumentException("Need at least one calibration observation.", nameof(observations));

        var values = observations.Select(o => o.Ppm).OrderBy(v => v).ToArray();
        double median = values.Length % 2 == 1
            ? values[values.Length / 2]
            : 0.5 * (values[values.Length / 2 - 1] + values[values.Length / 2]);
        return (int)Math.Round(median, MidpointRounding.AwayFromZero);
    }
}
