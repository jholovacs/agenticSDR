using AgenticSdr.Dsp;

namespace AgenticSdr.Tests;

/// <summary>Checks RDS CRC helpers and that a synthetic multiplex yields PI and station name.</summary>
public class RdsTests
{
    [Fact]
    public void Crc_round_trips_through_offset_words()
    {
        for (ushort data = 0; data < 64; data++)
        {
            for (int offset = 0; offset < 5; offset++)
            {
                int word = RdsDecoder.MakeBlock(data, offset);
                Assert.True(RdsDecoder.MatchBlock(word, out ushort payload, out int type));
                Assert.Equal(data, payload);
                Assert.Equal(offset == 3 ? 2 : offset == 4 ? 3 : offset, type);
            }
        }
    }

    [Fact]
    public void Us_pi_maps_to_four_letter_callsign()
    {
        Assert.True(RdsDecoder.TryUsCallsign(0x40E6, out string ksnm));
        Assert.Equal("KSNM", ksnm);
        Assert.False(RdsDecoder.TryUsCallsign(0xBEEF, out _));
    }

    [Fact]
    public void Synthetic_rds_prints_pi_and_name()
    {
        const double rate = 400_000;
        const double seconds = 2.5;
        int n = (int)(seconds * rate);

        // One 0A group: PI=0xBEEF, PS="KQED" in slots 0..3 (two group repeats fill 8 chars).
        ushort pi = 0xBEEF;
        ushort[] blockB =
        [
            (ushort)(0x0000 | 0), // type 0A, version A, offset 0
            (ushort)(0x0000 | 1),
            (ushort)(0x0000 | 2),
            (ushort)(0x0000 | 3)
        ];
        ushort[] blockD =
        [
            (ushort)(('K' << 8) | 'Q'),
            (ushort)(('E' << 8) | 'D'),
            (ushort)((' ' << 8) | ' '),
            (ushort)((' ' << 8) | ' ')
        ];

        var dataBits = new List<int>();
        for (int repeat = 0; repeat < 8; repeat++)
        {
            for (int g = 0; g < 4; g++)
            {
                AppendBlockBits(dataBits, RdsDecoder.MakeBlock(pi, 0));
                AppendBlockBits(dataBits, RdsDecoder.MakeBlock(blockB[g], 1));
                AppendBlockBits(dataBits, RdsDecoder.MakeBlock(0x0000, 2));
                AppendBlockBits(dataBits, RdsDecoder.MakeBlock(blockD[g], 4));
            }
        }

        var halves = new List<int>();
        RdsDecoder.EncodeBits(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(dataBits), halves);

        var mpx = new float[n];
        var cos3 = new float[n];
        var sin3 = new float[n];
        double pilot = 0;
        double pilotStep = 2 * Math.PI * 19_000 / rate;
        int halfIndex = 0;
        double samplesPerHalf = rate / (1_187.5 * 2);
        double halfPos = 0;
        int currentHalf = halves[0];

        for (int i = 0; i < n; i++)
        {
            pilot += pilotStep;
            double c = Math.Cos(pilot);
            double s = Math.Sin(pilot);
            cos3[i] = (float)(4 * c * c * c - 3 * c);
            sin3[i] = (float)(3 * s - 4 * s * s * s);

            halfPos += 1;
            if (halfPos >= samplesPerHalf)
            {
                halfPos -= samplesPerHalf;
                halfIndex = (halfIndex + 1) % halves.Count;
                currentHalf = halves[halfIndex];
            }

            // RDS carrier: ± level on cos(3φ). Quiet mono sum underneath.
            double rds = (currentHalf == 0 ? -1 : 1) * 0.12 * cos3[i];
            double sum = 0.2 * Math.Sin(2 * Math.PI * 800 * i / rate);
            double pilotTone = 0.1 * s;
            mpx[i] = (float)(sum + pilotTone + rds);
        }

        var decoder = new RdsDecoder(rate);
        const int chunk = 8_192;
        for (int offset = 0; offset < n; offset += chunk)
        {
            int len = Math.Min(chunk, n - offset);
            decoder.Push(mpx.AsSpan(offset, len), cos3.AsSpan(offset, len), sin3.AsSpan(offset, len));
        }

        IReadOnlyList<string> lines = decoder.Drain();
        string text = string.Join('\n', lines);
        Assert.Contains("BEEF", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("KQED", text, StringComparison.OrdinalIgnoreCase);
    }

    private static void AppendBlockBits(List<int> bits, int word26)
    {
        for (int i = 25; i >= 0; i--)
            bits.Add((word26 >> i) & 1);
    }
}
