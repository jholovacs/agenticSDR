using System.Numerics;

namespace AgenticSdr.Dsp;

/// <summary>
/// Builds a complex signal from one real channel by delaying that channel and adding a Hilbert (90-degree) companion.
/// Used when direct sampling gives only one usable wire of the antenna input.
/// </summary>
public static class AnalyticSignal
{
    public static Complex[] Promote(ReadOnlySpan<float> real)
    {
        var stream = new Stream();
        var iq = new Complex[real.Length];
        stream.Promote(real, iq);
        return iq;
    }

    /// <summary>
    /// Stateful Hilbert so FT8 Costas survives across USB read chunks. Rebuilding the FIR from
    /// scratch on every ~50 ms block shreds tones that last 160 ms.
    /// </summary>
    public sealed class Stream
    {
        private readonly float[] _taps = Filters.Hilbert(63);
        private readonly float[] _history;
        private readonly int _delay;
        private int _filled;

        public Stream()
        {
            _delay = _taps.Length / 2;
            _history = new float[_taps.Length];
        }

        public void Reset()
        {
            Array.Clear(_history);
            _filled = 0;
        }

        public void Promote(ReadOnlySpan<float> real, Span<Complex> iq)
        {
            int n = Math.Min(real.Length, iq.Length);
            for (int i = 0; i < n; i++)
            {
                // Shift history: newest sample at index 0.
                Array.Copy(_history, 0, _history, 1, _history.Length - 1);
                _history[0] = real[i];
                if (_filled < _history.Length)
                    _filled++;

                double imag = 0;
                int taps = Math.Min(_taps.Length, _filled);
                for (int k = 0; k < taps; k++)
                    imag += _taps[k] * _history[k];

                float re = _filled > _delay ? _history[_delay] : 0;
                iq[i] = new Complex(re, imag);
            }
        }
    }
}
