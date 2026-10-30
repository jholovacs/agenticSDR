using System.Numerics;
using AgenticSdr.Dsp;

namespace AgenticSdr.Tests;

/// <summary>Checks that a synthetic DMR framing word is recognized and that ordinary FM is not called DMR.</summary>
public class DmrTests
{
    [Fact]
    public void A_framing_word_is_recognized()
    {
        Complex[] iq = Burst(includeSync: true);
        Assert.True(DmrSync.Present(iq, 48_000, 0));
    }

    [Fact]
    public void Narrow_fm_voice_is_not_dmr()
    {
        const int rate = 48_000;
        int count = (int)(0.08 * rate);
        var iq = new Complex[count];
        double phase = 0;
        double modulator = 0;
        double modStep = 2 * Math.PI * 1_000 / rate;
        double deviation = 2 * Math.PI * 2_500 / rate;
        for (int i = 0; i < count; i++)
        {
            modulator += modStep;
            phase += deviation * Math.Sin(modulator);
            iq[i] = new Complex(Math.Cos(phase), Math.Sin(phase));
        }

        Assert.False(DmrSync.Present(iq, rate, 0));
    }

    private static Complex[] Burst(bool includeSync)
    {
        const int rate = 48_000;
        int[] sync = Levels("755FD7DF75F7");
        var symbols = new List<int>();
        for (int i = 0; i < 40; i++)
            symbols.Add(1);
        if (includeSync)
            symbols.AddRange(sync);
        for (int i = 0; i < 40; i++)
            symbols.Add(-1);

        var iq = new Complex[symbols.Count * 10];
        double phase = 0;
        int at = 0;
        foreach (int symbol in symbols)
        {
            double step = 2 * Math.PI * (symbol * 648.0) / rate;
            for (int sample = 0; sample < 10; sample++)
            {
                phase += step;
                iq[at++] = new Complex(Math.Cos(phase), Math.Sin(phase));
            }
        }

        return iq;
    }

    private static int[] Levels(string hex)
    {
        var bits = new List<int>();
        foreach (char c in hex)
        {
            int value = Convert.ToInt32(c.ToString(), 16);
            for (int shift = 3; shift >= 0; shift--)
                bits.Add((value >> shift) & 1);
        }

        var levels = new int[bits.Count / 2];
        for (int i = 0; i < levels.Length; i++)
        {
            int dibit = (bits[i * 2] << 1) | bits[i * 2 + 1];
            levels[i] = dibit switch
            {
                0b01 => 3,
                0b00 => 1,
                0b10 => -1,
                _ => -3
            };
        }

        return levels;
    }
}
