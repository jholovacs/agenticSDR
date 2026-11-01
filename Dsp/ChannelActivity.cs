using System.Numerics;

namespace AgenticSdr.Dsp;

/// <summary>
/// Decides whether a tuned station is present. The check is the energy in the station's channel
/// compared with an empty channel nearby, so a broadcast that is already on still counts.
/// </summary>
public static class ChannelActivity
{
    public static double WidthHz(RadioMode mode) => mode switch
    {
        RadioMode.Wbfm => 180_000,
        RadioMode.Am => 10_000,
        RadioMode.Usb or RadioMode.Lsb or RadioMode.Ft8 => 3_000,
        RadioMode.Cw => 800,
        RadioMode.Raw => 25_000,
        _ => 12_500
    };

    /// <summary>True when <paramref name="stationHz"/> is several times stronger than the nearby empty channels.</summary>
    public static bool Heard(ReadOnlySpan<Complex> iq, double sampleRate, double stationHz, double channelHz)
    {
        if (iq.Length < 64 || sampleRate <= 0 || channelHz < 100)
            return false;

        double step = Math.Max(channelHz * 3, 15_000);
        double signal = BinPower(iq, sampleRate, stationHz);
        double left = BinPower(iq, sampleRate, stationHz - step);
        double right = BinPower(iq, sampleRate, stationHz + step);
        double far = BinPower(iq, sampleRate, stationHz + step * 2);
        double other = Median(left, right, far);
        return signal > 1e-12 && signal > other * 8;
    }

    private static double Median(double a, double b, double c)
    {
        if (a > b)
            (a, b) = (b, a);
        if (b > c)
            (b, c) = (c, b);
        if (a > b)
            (a, b) = (b, a);
        return b;
    }

    private static double BinPower(ReadOnlySpan<Complex> iq, double sampleRate, double frequencyHz)
    {
        double highest = Math.Abs(frequencyHz) + 1;
        int stride = highest <= 0 ? 1 : Math.Max(1, (int)(sampleRate / (highest * 4)));
        stride = Math.Min(stride, Math.Max(1, iq.Length / 2048));
        double omega = 2 * Math.PI * frequencyHz / sampleRate * stride;
        double angle = 0;
        double real = 0;
        double imag = 0;
        int count = 0;
        for (int i = 0; i < iq.Length; i += stride)
        {
            angle -= omega;
            double cos = Math.Cos(angle);
            double sin = Math.Sin(angle);
            real += iq[i].Real * cos - iq[i].Imaginary * sin;
            imag += iq[i].Real * sin + iq[i].Imaginary * cos;
            count++;
        }

        if (count == 0)
            return 0;
        return (real * real + imag * imag) / count;
    }
}

/// <summary>Keeps a station "active" for a short time after its energy dips, so a pause between words does not mute the audio.</summary>
public sealed class ActivityHold
{
    private int _left;

    public bool Active { get; private set; }

    public void Observe(bool present, int samples, int sampleRate)
    {
        int hang = Math.Max(1, sampleRate / 2);
        if (present)
            _left = hang;
        else
            _left = Math.Max(0, _left - Math.Max(0, samples));
        Active = _left > 0;
    }
}
