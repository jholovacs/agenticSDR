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
    public void A_voice_header_names_the_talkgroup_and_the_alias()
    {
        var reader = new DmrReader();
        IReadOnlyList<string> lines = reader.Push(DataBurst(GroupVoice(talkgroup: 9, source: 100), color: 1), 48_000, 0, default);
        Assert.Contains(lines, line => line.Contains("Talkgroup 9 from 100") && line.Contains("color 1"));

        lines = reader.Push(DataBurst(Alias("NET"), color: 1), 48_000, 0, default);
        Assert.Contains(lines, line => line.Contains("Talker alias 100: NET"));
    }

    [Fact]
    public void Another_color_code_is_left_out()
    {
        var reader = new DmrReader();
        IReadOnlyList<string> lines = reader.Push(
            DataBurst(GroupVoice(9, 100), color: 1),
            48_000,
            0,
            new DmrFilter(2, null, null));
        Assert.DoesNotContain(lines, line => line.Contains("Talkgroup"));
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

    private static byte[] GroupVoice(int talkgroup, int source)
    {
        var info = new byte[12];
        info[5] = (byte)talkgroup;
        info[6] = (byte)((source >> 16) & 0xFF);
        info[7] = (byte)((source >> 8) & 0xFF);
        info[8] = (byte)(source & 0xFF);
        return info;
    }

    private static byte[] Alias(string text)
    {
        var info = new byte[12];
        info[0] = 0x04;
        info[2] = (byte)((1 << 6) | (text.Length << 1));
        for (int i = 0; i < text.Length; i++)
            info[3 + i] = (byte)text[i];
        return info;
    }

    private static Complex[] DataBurst(byte[] info, int color)
    {
        var bits = new int[96];
        for (int i = 0; i < 12; i++)
        {
            for (int b = 0; b < 8; b++)
                bits[i * 8 + b] = (info[i] >> (7 - b)) & 1;
        }

        int[] air = DmrReader.Protect(bits);
        int[] slot = DmrReader.SlotType(color, 1);
        int[] sync = HexBits("DFF57D75DF5D");
        var dibits = new int[132];
        Pack(dibits, 0, air[..98]);
        Pack(dibits, 49, slot[..10]);
        Pack(dibits, 54, sync);
        Pack(dibits, 78, slot[10..]);
        Pack(dibits, 83, air[98..]);

        const int rate = 48_000;
        var iq = new Complex[dibits.Length * 10];
        double phase = 0;
        int at = 0;
        foreach (int dibit in dibits)
        {
            int level = dibit switch
            {
                0b01 => 3,
                0b00 => 1,
                0b10 => -1,
                _ => -3
            };
            double step = 2 * Math.PI * (level * 648.0) / rate;
            for (int sample = 0; sample < 10; sample++)
            {
                phase += step;
                iq[at++] = new Complex(Math.Cos(phase), Math.Sin(phase));
            }
        }

        return iq;
    }

    private static void Pack(int[] dibits, int at, int[] bits)
    {
        for (int i = 0; i < bits.Length / 2; i++)
            dibits[at + i] = (bits[i * 2] << 1) | bits[i * 2 + 1];
    }

    private static int[] HexBits(string hex)
    {
        var bits = new List<int>();
        foreach (char c in hex)
        {
            int value = Convert.ToInt32(c.ToString(), 16);
            for (int shift = 3; shift >= 0; shift--)
                bits.Add((value >> shift) & 1);
        }

        return bits.ToArray();
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
