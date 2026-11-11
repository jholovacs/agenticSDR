namespace AgenticSdr.Dsp;

/// <summary>Streaming real FIR with optional decimation. Keeps history across Push chunks.</summary>
internal sealed class RealFir
{
    private readonly float[] _taps;
    private readonly int _factor;
    private readonly float[] _hist;
    private int _pos;
    private int _histLen;
    private int _phase;

    public RealFir(float[] taps, int factor = 1)
    {
        _taps = taps;
        _factor = Math.Max(1, factor);
        _hist = new float[taps.Length];
    }

    public void Process(ReadOnlySpan<float> input, List<float> output)
    {
        for (int i = 0; i < input.Length; i++)
        {
            if (Push(input[i], out float y))
                output.Add(y);
        }
    }

    /// <summary>Always returns the filtered sample (ignores decimation phase). Used when every sample is needed.</summary>
    public float Filter(float sample)
    {
        Store(sample);
        return Dot();
    }

    /// <summary>Stores a sample and returns true with an output when the decimation phase keeps it.</summary>
    public bool Push(float sample, out float output)
    {
        Store(sample);
        if (_phase == 0)
        {
            output = Dot();
            AdvancePhase();
            return true;
        }

        AdvancePhase();
        output = 0;
        return false;
    }

    private void Store(float sample)
    {
        _hist[_pos] = sample;
        if (_histLen < _hist.Length)
            _histLen++;
        if (++_pos >= _hist.Length)
            _pos = 0;
    }

    private float Dot()
    {
        float sum = 0;
        int n = Math.Min(_taps.Length, _histLen);
        // Newest sample sits at (_pos - 1); taps[0] multiplies the newest.
        int idx = _pos - 1;
        if (idx < 0)
            idx = _hist.Length - 1;
        for (int t = 0; t < n; t++)
        {
            sum += _taps[t] * _hist[idx];
            if (--idx < 0)
                idx = _hist.Length - 1;
        }

        return sum;
    }

    private void AdvancePhase()
    {
        _phase++;
        if (_phase >= _factor)
            _phase = 0;
    }
}
