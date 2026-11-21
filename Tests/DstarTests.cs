using System.Numerics;
using AgenticSdr.Dsp;
using AgenticSdr.Host;

namespace AgenticSdr.Tests;

/// <summary>D-STAR DV framing recognition and radio-header callsign decode (no AMBE).</summary>
public class DstarTests
{
    [Fact]
    public void A_frame_sync_is_recognized()
    {
        Complex[] iq = DstarSync.ModulateHeaderIq(0x40, "REF001BL", "W1AW  C", "CQCQCQ", "W1AW", "    ");
        Assert.True(DstarSync.Present(iq, 48_000, 0));
    }

    [Fact]
    public void A_radio_header_names_the_callsigns()
    {
        Complex[] iq = DstarSync.ModulateHeaderIq(0x40, "REF001BL", "W1AW  C", "CQCQCQ", "W1AW", "RPT ");
        var reader = new DstarReader();
        IReadOnlyList<string> lines = reader.Push(iq, 48_000, 0);
        Assert.Contains(lines, line => line.Contains("W1AW", StringComparison.Ordinal) && line.Contains("CQCQCQ", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("W1AW", StringComparison.Ordinal) && line.Contains("via", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Header_encode_round_trips_through_fec()
    {
        int[] coded = DstarReader.EncodeHeader(0x40, "REF001BL", "W1AW  C", "CQCQCQ", "N0CALL", "TEST");
        Assert.Equal(660, coded.Length);
        // Present() path uses IQ; here just check FrameSync + coded length composition for ModulateHeaderIq.
        var bits = new List<int>();
        for (int i = 0; i < 32; i++)
        {
            bits.Add(1);
            bits.Add(0);
        }

        bits.AddRange(DstarSync.FrameSync);
        bits.AddRange(coded);
        Complex[] iq = DstarSync.ModulateBits(bits);
        Assert.True(DstarSync.Present(iq, 48_000, 0));
        var reader = new DstarReader();
        IReadOnlyList<string> lines = reader.Push(iq, 48_000, 0);
        Assert.Contains(lines, line => line.Contains("N0CALL", StringComparison.Ordinal));
    }

    [Fact]
    public void Narrow_fm_voice_is_not_dstar()
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

        Assert.False(DstarSync.Present(iq, rate, 0));
    }

    [Fact]
    public void Mode_parse_and_camp_accept_dstar()
    {
        Assert.Equal(RadioMode.Dstar, ModeClassifier.Parse("dstar"));
        Assert.Equal(RadioMode.Dstar, ModeClassifier.Parse("d-star"));
        Assert.Equal("dstar", CampSchedule.Canonical(RadioMode.Dstar));
    }
}
