using System.Buffers;
using System.Numerics;

namespace AgenticSdr.Dsp;

/// <summary>
/// A streaming filter that keeps one output sample for every N input samples.
/// State is kept between chunks so a long listen does not click at the block boundaries.
/// </summary>
internal sealed class ComplexDecimator
{
    private readonly float[] _taps;
    private readonly int _factor;
    private readonly Complex[] _carry;
    private int _carryCount;

    public ComplexDecimator(float[] taps, int factor)
    {
        if (factor < 1)
            throw new ArgumentOutOfRangeException(nameof(factor));
        _taps = taps;
        _factor = factor;
        _carry = new Complex[Math.Max(1, taps.Length)];
    }

    public void Process(ReadOnlySpan<Complex> input, List<Complex> output)
    {
        if (input.Length == 0)
            return;

        int length = _carryCount + input.Length;
        Complex[] block = ArrayPool<Complex>.Shared.Rent(length);
        try
        {
            if (_carryCount > 0)
                _carry.AsSpan(0, _carryCount).CopyTo(block);
            input.CopyTo(block.AsSpan(_carryCount));

            int pos = 0;
            int taps = _taps.Length;
            while (pos + taps <= length)
            {
                float accR = 0;
                float accI = 0;
                int end = pos + taps;
                for (int k = pos, t = 0; k < end; k++, t++)
                {
                    float tap = _taps[t];
                    Complex sample = block[k];
                    accR += (float)sample.Real * tap;
                    accI += (float)sample.Imaginary * tap;
                }

                output.Add(new Complex(accR, accI));
                pos += _factor;
            }

            _carryCount = length - pos;
            if (_carryCount > 0)
                block.AsSpan(pos, _carryCount).CopyTo(_carry);
        }
        finally
        {
            ArrayPool<Complex>.Shared.Return(block);
        }
    }
}
