namespace AgenticSdr.Dsp;

/// <summary>
/// Finds stations in a series of short spectra taken while the radio stays on one frequency.
/// A conversation shows up as a burst, a pause, and another burst. A station that stays on,
/// such as weather radio or a broadcast, is kept when it is clearly above the noise.
/// The width of each candidate has to match the mode being searched. Morse and voice are
/// separate hits even when they fall in the same channel.
/// </summary>
public static class SpeechActivity
{
    public sealed record Hit(SpectralPeak Peak, bool Intermittent, RadioMode Mode);

    public static List<Hit> Find(
        IReadOnlyList<double[]> frames,
        double centerHz,
        double sampleRate,
        double startHz,
        double endHz,
        RadioMode mode,
        double frameSeconds,
        double thresholdDb = 6)
    {
        ModeBandwidth.Pass? pass = ModeBandwidth.For(mode);
        if (pass is null || frames.Count == 0 || frames[0].Length < 8 || sampleRate <= 0 || frameSeconds <= 0)
            return [];

        int bins = frames[0].Length;
        double binHz = sampleRate / bins;
        var noises = new double[frames.Count];
        for (int i = 0; i < frames.Count; i++)
            noises[i] = Median(frames[i]);

        int radius = Math.Max(1, (int)Math.Round(pass.Value.WidthHz / 2 / binHz));
        var found = new List<SpectralPeak>();
        var intermittent = new List<bool>();
        for (double channel = startHz; channel <= endHz + 1; channel += pass.Value.StepHz)
        {
            if (Math.Abs(channel - centerHz) < 2_000 + pass.Value.WidthHz / 2)
                continue;

            int centerBin = (int)Math.Round((channel - centerHz) / binHz + bins / 2.0);
            if (centerBin <= 0 || centerBin >= bins - 1)
                continue;

            var active = new bool[frames.Count];
            double bestSnr = 0;
            int bestBin = centerBin;
            double bestWidth = 0;
            double bestPower = 0;
            double bestNoise = 0;
            for (int frame = 0; frame < frames.Count; frame++)
            {
                int peakBin = Loudest(frames[frame], centerBin, radius, centerHz, binHz);
                if (peakBin < 0)
                    continue;
                double snr = frames[frame][peakBin] - noises[frame];
                double width = ContiguousHz(frames[frame], peakBin, noises[frame], centerHz, binHz);
                if (snr < thresholdDb || width < pass.Value.MinHz || width > pass.Value.MaxHz)
                    continue;
                active[frame] = true;
                if (snr > bestSnr)
                {
                    bestSnr = snr;
                    bestBin = peakBin;
                    bestWidth = width;
                    bestPower = frames[frame][peakBin];
                    bestNoise = noises[frame];
                }
            }

            if (!Kept(active, frameSeconds, bestSnr, out bool pulsed))
                continue;

            double freq = centerHz + (bestBin - bins / 2.0) * binHz;
            double half = bestWidth / 2;
            found.Add(new SpectralPeak(freq, bestPower, bestNoise, bestSnr, bestWidth, freq - half, freq + half, bestSnr, 0, false));
            intermittent.Add(pulsed);
        }

        List<SpectralPeak> merged = PeakPicker.Merge(found, pass.Value.WidthHz);
        var hits = new List<Hit>(merged.Count);
        foreach (SpectralPeak peak in merged)
        {
            int index = found.FindIndex(item => Math.Abs(item.FrequencyHz - peak.FrequencyHz) < 1);
            bool pulsed = index >= 0 && intermittent[index];
            hits.Add(new Hit(peak, pulsed, mode));
        }

        return hits;
    }

    /// <summary>
    /// Keeps the louder of two hits only when they are the same mode and close together.
    /// A Morse signal is left beside a voice signal in the same channel.
    /// </summary>
    public static List<Hit> Distinct(IEnumerable<Hit> hits)
    {
        var unique = new List<Hit>();
        foreach (Hit hit in hits.OrderByDescending(hit => hit.Peak.SnrDb))
        {
            if (unique.Any(kept => SameStation(kept, hit)))
                continue;
            unique.Add(hit);
        }

        return unique;
    }

    private static bool SameStation(Hit a, Hit b)
    {
        if (a.Mode != b.Mode)
            return false;
        double limit = a.Mode == RadioMode.Cw ? 1_000 : 8_000;
        return Math.Abs(a.Peak.FrequencyHz - b.Peak.FrequencyHz) < limit;
    }

    private static bool Kept(bool[] active, double frameSeconds, double bestSnr, out bool intermittent)
    {
        intermittent = false;
        int runs = 0;
        int activeCount = 0;
        int longest = 0;
        int run = 0;
        bool on = false;
        foreach (bool bit in active)
        {
            if (!bit)
            {
                on = false;
                continue;
            }

            activeCount++;
            if (!on)
            {
                runs++;
                on = true;
                run = 1;
            }
            else
            {
                run++;
            }

            if (run > longest)
                longest = run;
        }

        if (activeCount == 0)
            return false;

        double activeSeconds = activeCount * frameSeconds;
        bool covered = activeCount >= active.Length * 0.85;
        if (covered)
            return bestSnr >= 8 && activeSeconds >= 1;

        bool pulsed = runs >= 2 && activeSeconds >= 0.4;
        bool oneTransmission = runs == 1 && longest * frameSeconds >= 0.8;
        intermittent = pulsed || oneTransmission;
        return intermittent;
    }

    private static int Loudest(double[] db, int centerBin, int radius, double centerHz, double binHz)
    {
        int n = db.Length;
        int lo = Math.Max(0, centerBin - radius);
        int hi = Math.Min(n - 1, centerBin + radius);
        int best = -1;
        double bestDb = double.NegativeInfinity;
        for (int i = lo; i <= hi; i++)
        {
            double freq = centerHz + (i - n / 2.0) * binHz;
            if (Math.Abs(freq - centerHz) < 2_000)
                continue;
            if (db[i] > bestDb)
            {
                bestDb = db[i];
                best = i;
            }
        }

        return best;
    }

    private static double ContiguousHz(double[] db, int peakBin, double noise, double centerHz, double binHz)
    {
        int n = db.Length;
        double gate = noise + 4;
        int left = peakBin;
        while (left > 0 && db[left - 1] >= gate && Math.Abs(centerHz + ((left - 1) - n / 2.0) * binHz - centerHz) >= 2_000)
            left--;
        int right = peakBin;
        while (right < n - 1 && db[right + 1] >= gate && Math.Abs(centerHz + ((right + 1) - n / 2.0) * binHz - centerHz) >= 2_000)
            right++;
        return Math.Max(binHz, (right - left + 1) * binHz);
    }

    private static double Median(double[] values)
    {
        var copy = (double[])values.Clone();
        Array.Sort(copy);
        int mid = copy.Length / 2;
        if (copy.Length % 2 == 1)
            return copy[mid];
        return (copy[mid - 1] + copy[mid]) / 2;
    }
}
