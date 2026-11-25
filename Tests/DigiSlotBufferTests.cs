using AgenticSdr.Dsp;

namespace AgenticSdr.Tests;

public class DigiSlotBufferTests
{
    [Theory]
    [InlineData(0.0, 30, 0)]
    [InlineData(14.0, 30, 0)]
    [InlineData(29.0, 30, 0)]
    [InlineData(29.55, 30, 1)] // early pad → next slot
    [InlineData(30.0, 30, 1)]
    [InlineData(44.0, 15, 2)]
    [InlineData(14.55, 15, 1)]
    public void TargetSlot_advances_on_early_pad(double tod, double period, int expected)
    {
        Assert.Equal(expected, DigiSlotBuffer.TargetSlot(tod, period));
    }

    [Fact]
    public void TryTakeFinished_keeps_near_full_early_armed_chop()
    {
        // Early-arm start: collect for ~29.6 s of a 30 s step, then the next early-pad fires.
        const int rate = 48_000;
        const int step = 30 * rate;
        var period = new List<float>(Enumerable.Repeat(0.25f, (int)(step * 0.92)));

        Assert.True(DigiSlotBuffer.TryTakeFinished(period, step, out float[] block));
        Assert.Equal(period.Count, block.Length);

        // Old bug behavior would have cleared without submitting (< 100% step).
        Assert.False(DigiSlotBuffer.TryTakeFinished(new List<float>(Enumerable.Repeat(0f, step / 2)), step, out _));
    }

    [Fact]
    public void TryTakeFinished_prefers_last_full_step_when_overshot()
    {
        const int step = 1000;
        var period = new List<float>();
        for (int i = 0; i < step + 200; i++)
            period.Add(i);

        Assert.True(DigiSlotBuffer.TryTakeFinished(period, step, out float[] block));
        Assert.Equal(step, block.Length);
        Assert.Equal(200f, block[0]);
        Assert.Equal(step + 199f, block[^1]);
    }

    [Fact]
    public void Early_pad_slot_change_must_not_drop_a_full_period()
    {
        // Simulate the live loop: arm in early pad, collect one period, next early pad arrives.
        const double periodSec = 30;
        const int rate = 1_000; // small for the test
        int step = (int)(periodSec * rate);
        var buf = new List<float>();
        int? collecting = null;
        var submitted = new List<int>();

        for (double t = 29.55; t < 29.55 + periodSec + 0.5; t += 0.05)
        {
            int target = DigiSlotBuffer.TargetSlot(t, periodSec);
            collecting ??= target;
            if (target != collecting)
            {
                if (DigiSlotBuffer.TryTakeFinished(buf, step, out float[] block))
                    submitted.Add(block.Length);
                buf.Clear();
                collecting = target;
            }

            int samples = (int)(0.05 * rate);
            for (int i = 0; i < samples; i++)
                buf.Add(1f);
        }

        Assert.NotEmpty(submitted);
        Assert.Contains(submitted, n => n >= (int)(step * 0.85));
    }
}
