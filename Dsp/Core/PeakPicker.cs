namespace AgenticSdr.Dsp;

/// <summary>
/// Finds peaks in a spectrum. A peak must stand above the noise, sit away from the center-frequency spike, and have a width inside the requested limits.
/// The reported frequency is the loudest bin, so a lopsided station is not tuned to the middle of its skirt.
/// Broadcast FM uses a wider limit so one station stays one peak instead of a row of narrow spikes.
/// </summary>
public static class PeakPicker
{
    public static List<SpectralPeak> Find(
        double[] powerDb,
        double centerHz,
        double sampleRate,
        double thresholdDb = 10,
        double maxBandwidthHz = 20_000,
        double dcIgnoreHz = 2_000,
        bool collapseImages = false,
        double contourDropDb = 18,
        double minBandwidthHz = 0)
    {
        int n = powerDb.Length;
        if (n < 8)
            return [];

        double noise = Median(powerDb);
        double binHz = sampleRate / n;
        var candidates = new List<SpectralPeak>();
        for (int i = 1; i < n - 1; i++)
        {
            if (powerDb[i] < powerDb[i - 1] || powerDb[i] <= powerDb[i + 1])
                continue;

            double snr = powerDb[i] - noise;
            if (snr < thresholdDb)
                continue;

            double freq = centerHz + (i - n / 2.0) * binHz;
            if (Math.Abs(freq - centerHz) < dcIgnoreHz)
                continue;

            double limit = Math.Max(noise + 8, powerDb[i] - contourDropDb);
            int left = i;
            while (left > 0 && powerDb[left - 1] >= limit)
                left--;
            int right = i;
            while (right < n - 1 && powerDb[right + 1] >= limit)
                right++;

            double low = centerHz + (left - n / 2.0) * binHz;
            double high = centerHz + (right - n / 2.0) * binHz;
            double bandwidth = Math.Max(binHz, high - low);
            if (bandwidth > maxBandwidthHz || bandwidth < minBandwidthHz)
                continue;

            double prominence = Prominence(powerDb, i, left, right);
            double asymmetry = Asymmetry(powerDb, centerHz, sampleRate, freq);
            candidates.Add(new SpectralPeak(
                freq,
                powerDb[i],
                noise,
                snr,
                bandwidth,
                low,
                high,
                prominence,
                asymmetry,
                PossibleImage: false));
        }

        var kept = SuppressOverlaps(candidates);
        if (collapseImages)
            kept = CollapseImages(kept, centerHz, binHz);
        kept.Sort((a, b) => b.SnrDb.CompareTo(a.SnrDb));
        return kept;
    }

    public static List<SpectralPeak> Merge(IReadOnlyList<SpectralPeak> peaks, double withinHz)
    {
        var merged = new List<SpectralPeak>();
        foreach (var peak in peaks.OrderBy(p => p.FrequencyHz))
        {
            if (merged.Count > 0 && Math.Abs(merged[^1].FrequencyHz - peak.FrequencyHz) <= withinHz)
            {
                if (peak.SnrDb > merged[^1].SnrDb)
                    merged[^1] = peak;
            }
            else
            {
                merged.Add(peak);
            }
        }

        return merged;
    }

    /// <summary>
    /// Loudest bin inside a published channel. Voice is often a few kilohertz wide, so the exact center bin can sit in a dip while the station is on a neighbor.
    /// </summary>
    public static SpectralPeak? LoudestIn(
        double[] powerDb,
        double centerHz,
        double sampleRate,
        double channelHz,
        double halfWidthHz,
        double minSnrDb)
    {
        if (powerDb.Length < 8 || halfWidthHz <= 0 || sampleRate <= 0)
            return null;

        double noise = Median(powerDb);
        double binHz = sampleRate / powerDb.Length;
        int center = (int)Math.Round((channelHz - centerHz) / binHz + powerDb.Length / 2.0);
        int radius = Math.Max(1, (int)Math.Round(halfWidthHz / binHz));
        int lo = Math.Max(0, center - radius);
        int hi = Math.Min(powerDb.Length - 1, center + radius);
        int best = -1;
        double bestDb = double.NegativeInfinity;
        for (int i = lo; i <= hi; i++)
        {
            if (powerDb[i] > bestDb)
            {
                bestDb = powerDb[i];
                best = i;
            }
        }

        if (best < 0)
            return null;
        double snr = bestDb - noise;
        if (snr < minSnrDb)
            return null;

        double freq = centerHz + (best - powerDb.Length / 2.0) * binHz;
        return new SpectralPeak(freq, bestDb, noise, snr, binHz, freq - binHz / 2, freq + binHz / 2, snr, 0, false);
    }

    private static List<SpectralPeak> SuppressOverlaps(List<SpectralPeak> peaks)
    {
        var ordered = peaks.OrderByDescending(p => p.SnrDb).ToList();
        var kept = new List<SpectralPeak>();
        foreach (var peak in ordered)
        {
            bool inside = kept.Any(k =>
                peak.FrequencyHz >= k.LowHz && peak.FrequencyHz <= k.HighHz);
            if (!inside)
                kept.Add(peak);
        }

        return kept;
    }

    private static List<SpectralPeak> CollapseImages(List<SpectralPeak> peaks, double centerHz, double binHz)
    {
        var used = new bool[peaks.Count];
        var kept = new List<SpectralPeak>();
        for (int i = 0; i < peaks.Count; i++)
        {
            if (used[i])
                continue;

            double mirror = 2 * centerHz - peaks[i].FrequencyHz;
            int pair = -1;
            for (int j = i + 1; j < peaks.Count; j++)
            {
                if (used[j])
                    continue;
                if (Math.Abs(peaks[j].FrequencyHz - mirror) <= Math.Max(binHz * 2, 1_500)
                    && Math.Abs(peaks[j].SnrDb - peaks[i].SnrDb) < 4)
                {
                    pair = j;
                    break;
                }
            }

            if (pair < 0)
            {
                kept.Add(peaks[i]);
                continue;
            }

            used[pair] = true;
            var primary = Math.Abs(peaks[i].FrequencyHz - centerHz) >= Math.Abs(peaks[pair].FrequencyHz - centerHz)
                ? peaks[i]
                : peaks[pair];
            kept.Add(primary with { PossibleImage = true });
        }

        return kept;
    }

    private static double Prominence(double[] powerDb, int peak, int left, int right)
    {
        double sum = 0;
        int count = 0;
        for (int i = left; i <= right; i++)
        {
            if (Math.Abs(i - peak) <= 1)
                continue;
            sum += powerDb[i];
            count++;
        }

        if (count == 0)
            return 20;
        return powerDb[peak] - sum / count;
    }

    private static double Asymmetry(double[] powerDb, double centerHz, double sampleRate, double peakHz)
    {
        int n = powerDb.Length;
        double binHz = sampleRate / n;
        double left = 0;
        double right = 0;
        for (int i = 0; i < n; i++)
        {
            double freq = centerHz + (i - n / 2.0) * binHz;
            double delta = freq - peakHz;
            double abs = Math.Abs(delta);
            if (abs < 200 || abs > 3_000)
                continue;
            double linear = Math.Pow(10, powerDb[i] / 10);
            if (delta > 0)
                right += linear;
            else
                left += linear;
        }

        double total = left + right;
        if (total <= 0)
            return 0;
        return (right - left) / total;
    }

    private static double Median(double[] values)
    {
        var copy = (double[])values.Clone();
        Array.Sort(copy);
        int mid = copy.Length / 2;
        if (copy.Length % 2 == 1)
            return copy[mid];
        return 0.5 * (copy[mid - 1] + copy[mid]);
    }
}
