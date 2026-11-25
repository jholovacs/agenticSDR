namespace AgenticSdr.Dsp;

/// <summary>
/// UTC digimode slot bookkeeping for live FT8 / JS8 / WSPR chops.
/// Capture arms slightly early so Costas is not clipped; finishing a period must submit
/// before the buffer is cleared for the next slot.
/// </summary>
public static class DigiSlotBuffer
{
    public const double EarlyPadSeconds = 0.45;

    /// <summary>
    /// Slot index we should be collecting for at <paramref name="todSeconds"/> (UTC time-of-day).
    /// In the last <see cref="EarlyPadSeconds"/> of a period this advances to the upcoming slot.
    /// </summary>
    public static int TargetSlot(double todSeconds, double periodSeconds)
    {
        if (periodSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(periodSeconds));
        double into = todSeconds % periodSeconds;
        if (into < 0)
            into += periodSeconds;
        int wallSlot = (int)Math.Floor(todSeconds / periodSeconds);
        double earlyAt = periodSeconds - EarlyPadSeconds;
        return into >= earlyAt ? wallSlot + 1 : wallSlot;
    }

    /// <summary>
    /// True while we should arm / keep a decode buffer (early pad or just after the boundary).
    /// </summary>
    public static bool ShouldArm(double todSeconds, double periodSeconds)
    {
        if (periodSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(periodSeconds));
        double into = todSeconds % periodSeconds;
        if (into < 0)
            into += periodSeconds;
        return into >= periodSeconds - EarlyPadSeconds || into < 0.20;
    }

    /// <summary>
    /// Take a finished period from <paramref name="period"/> when the target slot advances.
    /// Accepts ≥85% of <paramref name="step"/> so an early-armed chop that ends on the next
    /// early-pad is not discarded a few hundred milliseconds short of a full step.
    /// </summary>
    public static bool TryTakeFinished(List<float> period, int step, out float[] block)
    {
        ArgumentNullException.ThrowIfNull(period);
        block = [];
        if (step <= 0)
            return false;

        int minKeep = Math.Max(1, (int)(step * 0.85));
        if (period.Count < minKeep)
            return false;

        if (period.Count >= step)
        {
            block = period.GetRange(period.Count - step, step).ToArray();
            return true;
        }

        block = period.ToArray();
        return true;
    }
}
