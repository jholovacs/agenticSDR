namespace AgenticSdr.Dsp;

/// <summary>
/// Chooses how to divide a sample rate down to audio near 48 kHz.
/// The 2.4 MS/s RTL-SDR path does not use this; it keeps its original two filters so that path stays unchanged.
/// </summary>
public static class DemodRates
{
    public static (int First, int Second, int AudioRate) Plan(double inputRate)
    {
        if (inputRate < 24_000)
            throw new ArgumentOutOfRangeException(nameof(inputRate), "Sample rate is too low to demodulate.");

        int best = 1;
        double bestError = double.MaxValue;
        int limit = Math.Max(1, (int)(inputRate / 24_000));
        for (int factor = 1; factor <= limit; factor++)
        {
            double audio = inputRate / factor;
            if (audio < 24_000 || audio > 96_000)
                continue;
            double error = Math.Abs(audio - 48_000);
            if (error < bestError - 0.5)
            {
                bestError = error;
                best = factor;
            }
        }

        int first = best;
        int second = 1;
        int root = (int)Math.Sqrt(best);
        for (int factor = root; factor >= 2; factor--)
        {
            if (best % factor != 0)
                continue;
            first = best / factor;
            second = factor;
            if (first < second)
                (first, second) = (second, first);
            break;
        }

        return (first, second, (int)Math.Round(inputRate / best));
    }
}
