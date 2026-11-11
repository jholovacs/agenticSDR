using AgenticSdr.Radio;

namespace AgenticSdr.Host;

/// <summary>
/// Frequencies to visit while scanning a band. Each hop overlaps the next so a signal hidden by the spike at the center of one capture is visible in a neighbor.
/// A band narrower than one capture is visited once, tuned beside the middle so the center spike is not sitting on the band.
/// </summary>
public static class HopPlanner
{
    public static IReadOnlyList<double> Centers(double startHz, double endHz, double sampleRateHz, double stepHz)
    {
        if (endHz <= startHz)
            throw new ArgumentException("Band end must be above the start.");

        double half = sampleRateHz / 2;
        if (endHz - startHz <= sampleRateHz)
        {
            double mid = (startHz + endHz) / 2;
            double room = half - (endHz - startHz) / 2;
            // A band this narrow is visited once. Shift the tune so the spike at the center of the capture falls outside the band.
            double shift = Math.Min(100_000, room * 0.5);
            return shift >= 5_000 ? [mid + shift] : [mid];
        }

        double first = startHz + half;
        double last = endHz - half;
        var centers = new List<double>();
        for (double cursor = first; cursor < last; cursor += stepHz)
            centers.Add(cursor);
        if (centers.Count == 0 || last - centers[^1] > 1_000)
            centers.Add(last);
        return centers;
    }

    /// <summary>The whole band is watched for at least 15 seconds. Each hop stays at least 3 seconds so a transmission can start.</summary>
    public const int MinBandMilliseconds = 15_000;

    public const int MinHopMilliseconds = 3_000;

    public const int SliceMilliseconds = 100;

    public static int DwellMilliseconds(int hops) =>
        hops < 1 ? MinBandMilliseconds : Math.Max(MinBandMilliseconds / hops, MinHopMilliseconds);
}
