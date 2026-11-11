namespace AgenticSdr.Dsp;

/// <summary>
/// Score for FT8-like occupancy above a published dial: several narrow tones in the USB window, not one carrier.
/// </summary>
public readonly record struct Ft8Score(bool Active, double SnrDb, double Occupancy, int HotBins, int Clusters);

/// <summary>
/// Finds the low-SNR FT8 pattern around a dial frequency without decoding messages.
/// FT8 fills dial+200 Hz through about dial+3 kHz with many short tones; speech scan thresholds miss that comb.
/// </summary>
public static class Ft8Activity
{
    public const double AudioLowHz = 200;
    public const double DefaultToleranceHz = 3_600;
    public const double AboveNoiseDb = 1.5;
    public const int MinHotBins = 4;
    public const int MinClusters = 3;

    public static Ft8Score Score(
        double[] powerDb,
        double centerHz,
        double sampleRate,
        double dialHz,
        double toleranceHz = DefaultToleranceHz)
    {
        int n = powerDb.Length;
        if (n < 32 || sampleRate <= 0 || toleranceHz < 500)
            return new Ft8Score(false, -40, 0, 0, 0);

        double binHz = sampleRate / n;
        double windowLow = dialHz + AudioLowHz;
        double windowHigh = dialHz + toleranceHz;
        var noiseBins = new List<double>(n);
        var window = new List<(int Index, double Db)>();
        for (int i = 0; i < n; i++)
        {
            double freq = centerHz + (i - n / 2.0) * binHz;
            if (Math.Abs(freq - centerHz) < 2_000)
                continue;
            if (freq >= windowLow && freq <= windowHigh)
                window.Add((i, powerDb[i]));
            else
                noiseBins.Add(powerDb[i]);
        }

        if (window.Count < 8 || noiseBins.Count < 8)
            return new Ft8Score(false, -40, 0, 0, 0);

        double noise = Median(noiseBins);
        double gate = noise + AboveNoiseDb;
        int hotBins = 0;
        int clusters = 0;
        bool inRun = false;
        double hotSum = 0;
        double best = double.NegativeInfinity;
        foreach ((int _, double db) in window)
        {
            if (db >= gate)
            {
                hotBins++;
                hotSum += db;
                if (db > best)
                    best = db;
                if (!inRun)
                {
                    clusters++;
                    inRun = true;
                }
            }
            else
            {
                inRun = false;
            }
        }

        double occupancy = hotBins / (double)window.Count;
        double snrDb = hotBins == 0 ? best - noise : (hotSum / hotBins) - noise;
        if (!double.IsFinite(snrDb))
            snrDb = -40;
        snrDb = Math.Clamp(snrDb, -40, 60);

        // Prefer a comb (several separated runs). With coarse bins, a filled window still counts if occupancy is spread.
        bool comb = hotBins >= MinHotBins && clusters >= MinClusters;
        bool filled = hotBins >= Math.Max(MinHotBins + 2, window.Count / 5) && occupancy >= 0.12 && snrDb >= 0;
        bool active = (comb || filled) && snrDb >= 0;
        return new Ft8Score(active, snrDb, occupancy, hotBins, clusters);
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        int mid = values.Count / 2;
        return values.Count % 2 == 0
            ? 0.5 * (values[mid - 1] + values[mid])
            : values[mid];
    }
}
