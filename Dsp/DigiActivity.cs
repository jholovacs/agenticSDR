namespace AgenticSdr.Dsp;

/// <summary>
/// Occupancy scores for published digimode dials. Speech scan misses weak multi-tone USB windows;
/// each dial is probed with a fine FFT and scored here. A hit means the watering hole looks busy —
/// it is not a claim that text will decode.
/// </summary>
public static class DigiActivity
{
    public static Ft8Score Score(
        RadioMode mode,
        double[] powerDb,
        double centerHz,
        double sampleRate,
        double dialHz,
        double toleranceHz)
    {
        return mode switch
        {
            RadioMode.Ft8 or RadioMode.Js8 => Ft8Activity.Score(powerDb, centerHz, sampleRate, dialHz, toleranceHz),
            // VARA HF is a ~2.3 kHz modem; require a wider, stronger footprint than a couple noise spikes.
            RadioMode.Varac => ScoreWindow(
                powerDb,
                centerHz,
                sampleRate,
                dialHz + 200,
                dialHz + Math.Min(toleranceHz, 2_500),
                minHotBins: 8,
                minClusters: 1,
                aboveNoiseDb: 4.0),
            RadioMode.Olivia => ScoreWindow(
                powerDb,
                centerHz,
                sampleRate,
                dialHz + OliviaDecoder.CenterHz - OliviaDecoder.BandwidthHz * 0.5,
                dialHz + OliviaDecoder.CenterHz + OliviaDecoder.BandwidthHz * 0.5,
                minHotBins: 3,
                minClusters: 2,
                aboveNoiseDb: 2.0),
            // JTTY is ~127 Hz wide; a multi-kHz energy gate was marking voice/noise as JTTY.
            RadioMode.Jtty => ScoreNarrow(
                powerDb,
                centerHz,
                sampleRate,
                dialHz + 400,
                dialHz + Math.Min(toleranceHz, 2_500),
                JttyDecoder.SpanHz * 1.5,
                minHotBins: 2,
                aboveNoiseDb: 3.0),
            _ => new Ft8Score(false, -40, 0, 0, 0)
        };
    }

    /// <summary>
    /// Scan-table confidence for digi dial occupancy only (not decode quality).
    /// Caps well below voice/FT8 decode confidence so 75% never means “we can read the text.”
    /// </summary>
    public static double ActivityConfidence(Ft8Score score)
    {
        if (!score.Active)
            return 0;
        double snr = Math.Clamp(score.SnrDb, 0, 20);
        double occupy = Math.Clamp(score.Occupancy, 0, 1);
        double confidence = 0.28 + 0.012 * snr + 0.12 * occupy;
        return Math.Round(Math.Clamp(confidence, 0.28, 0.52), 2);
    }

    private static Ft8Score ScoreWindow(
        double[] powerDb,
        double centerHz,
        double sampleRate,
        double windowLow,
        double windowHigh,
        int minHotBins,
        int minClusters,
        double aboveNoiseDb)
    {
        if (!TryWindowStats(powerDb, centerHz, sampleRate, windowLow, windowHigh, aboveNoiseDb,
                out double noise, out int hotBins, out int clusters, out double hotSum, out double best, out int windowCount))
            return new Ft8Score(false, -40, 0, 0, 0);

        double occupancy = hotBins / (double)windowCount;
        double snrDb = hotBins == 0 ? best - noise : (hotSum / hotBins) - noise;
        if (!double.IsFinite(snrDb))
            snrDb = -40;
        bool active = hotBins >= minHotBins && clusters >= minClusters && snrDb >= aboveNoiseDb;
        return new Ft8Score(active, snrDb, occupancy, hotBins, clusters);
    }

    /// <summary>
    /// Require at least one hot run whose width is near <paramref name="maxClusterHz"/> —
    /// used for JTTY's ~127 Hz footprint inside a wider search passband.
    /// </summary>
    private static Ft8Score ScoreNarrow(
        double[] powerDb,
        double centerHz,
        double sampleRate,
        double windowLow,
        double windowHigh,
        double maxClusterHz,
        int minHotBins,
        double aboveNoiseDb)
    {
        int n = powerDb.Length;
        if (n < 32 || sampleRate <= 0 || windowHigh - windowLow < 80)
            return new Ft8Score(false, -40, 0, 0, 0);

        double binHz = sampleRate / n;
        var noiseBins = new List<double>(n);
        var windowDb = new List<(double Freq, double Db)>();
        for (int i = 0; i < n; i++)
        {
            double freq = centerHz + (i - n / 2.0) * binHz;
            if (Math.Abs(freq - centerHz) < 2_000)
                continue;
            if (freq >= windowLow && freq <= windowHigh)
                windowDb.Add((freq, powerDb[i]));
            else
                noiseBins.Add(powerDb[i]);
        }

        if (windowDb.Count < 4 || noiseBins.Count < 8)
            return new Ft8Score(false, -40, 0, 0, 0);

        noiseBins.Sort();
        double noise = noiseBins[noiseBins.Count / 2];
        double gate = noise + aboveNoiseDb;
        int hotBins = 0;
        double hotSum = 0;
        double best = double.NegativeInfinity;
        int runStart = -1;
        int narrowClusters = 0;
        for (int i = 0; i <= windowDb.Count; i++)
        {
            bool hot = i < windowDb.Count && windowDb[i].Db >= gate;
            if (hot)
            {
                if (runStart < 0)
                    runStart = i;
                hotBins++;
                hotSum += windowDb[i].Db;
                if (windowDb[i].Db > best)
                    best = windowDb[i].Db;
                continue;
            }

            if (runStart >= 0)
            {
                double width = windowDb[i - 1].Freq - windowDb[runStart].Freq + binHz;
                if (width >= 40 && width <= maxClusterHz)
                    narrowClusters++;
                runStart = -1;
            }
        }

        double occupancy = hotBins / (double)windowDb.Count;
        double snrDb = hotBins == 0 ? best - noise : (hotSum / hotBins) - noise;
        if (!double.IsFinite(snrDb))
            snrDb = -40;
        bool active = hotBins >= minHotBins && narrowClusters >= 1 && snrDb >= aboveNoiseDb;
        return new Ft8Score(active, snrDb, occupancy, hotBins, narrowClusters);
    }

    private static bool TryWindowStats(
        double[] powerDb,
        double centerHz,
        double sampleRate,
        double windowLow,
        double windowHigh,
        double aboveNoiseDb,
        out double noise,
        out int hotBins,
        out int clusters,
        out double hotSum,
        out double best,
        out int windowCount)
    {
        noise = -40;
        hotBins = 0;
        clusters = 0;
        hotSum = 0;
        best = double.NegativeInfinity;
        windowCount = 0;
        int n = powerDb.Length;
        if (n < 32 || sampleRate <= 0 || windowHigh - windowLow < 80)
            return false;

        double binHz = sampleRate / n;
        var noiseBins = new List<double>(n);
        var window = new List<double>();
        for (int i = 0; i < n; i++)
        {
            double freq = centerHz + (i - n / 2.0) * binHz;
            if (Math.Abs(freq - centerHz) < 2_000)
                continue;
            if (freq >= windowLow && freq <= windowHigh)
                window.Add(powerDb[i]);
            else
                noiseBins.Add(powerDb[i]);
        }

        if (window.Count < 4 || noiseBins.Count < 8)
            return false;

        noiseBins.Sort();
        noise = noiseBins[noiseBins.Count / 2];
        double gate = noise + aboveNoiseDb;
        bool inRun = false;
        foreach (double db in window)
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

        windowCount = window.Count;
        return true;
    }
}
