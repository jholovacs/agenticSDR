namespace AgenticSdr.Dsp;

/// <summary>
/// Cleans voice audio before it is played or passed to speech-to-text.
/// A single-sample pop is replaced, energy outside the speech band is turned down, and a pause
/// that is much quieter than the recent voice is ducked so the hiss between words does not take over.
/// Broadcast FM keeps a wider band so music is not squeezed into a telephone channel.
/// </summary>
public sealed class VoiceCleaner
{
    private readonly Biquad _high;
    private readonly Biquad _high2;
    private readonly Biquad _low;
    private readonly Biquad _low2;
    private readonly float _peakRelease;
    private float _delay;
    private float _lastGood;
    private float _limit = 0.05f;
    private float _peak = 1e-4f;
    private bool _hasDelay;

    public VoiceCleaner(int sampleRate, double lowHz, double highHz)
    {
        if (sampleRate < 8_000)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        _high = Biquad.Highpass(sampleRate, lowHz);
        _high2 = Biquad.Highpass(sampleRate, lowHz);
        _low = Biquad.Lowpass(sampleRate, highHz);
        _low2 = Biquad.Lowpass(sampleRate, highHz);
        _peakRelease = (float)Math.Exp(-1.0 / (0.30 * sampleRate));
    }

    public static VoiceCleaner For(RadioMode mode, int sampleRate) =>
        mode == RadioMode.Wbfm
            ? new VoiceCleaner(sampleRate, 80, 8_000)
            : new VoiceCleaner(sampleRate, 300, 3_400);

    /// <summary>
    /// Analog modes where speech is expected: narrow FM, broadcast FM, AM, and sideband.
    /// Morse, FT8, and DMR are not cleaned and are not sent to speech-to-text.
    /// </summary>
    public static bool IsVoice(RadioMode mode) =>
        mode is RadioMode.Nbfm or RadioMode.Wbfm or RadioMode.Am or RadioMode.Usb or RadioMode.Lsb;

    public void Process(Span<float> samples)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            float incoming = samples[i];
            float emit = incoming;
            if (_hasDelay)
            {
                float jump = Math.Abs(_delay - _lastGood);
                bool pop = jump > _limit && Math.Abs(incoming - _lastGood) < _limit * 0.45f;
                emit = pop ? _lastGood : _delay;
                if (!pop)
                {
                    _limit = Math.Max(0.02f, _limit * 0.98f + jump * 0.16f);
                    _lastGood = _delay;
                }
            }
            else
            {
                _lastGood = incoming;
            }

            _delay = incoming;
            _hasDelay = true;

            float shaped = _low2.Process(_low.Process(_high2.Process(_high.Process(emit))));
            float mag = Math.Abs(shaped);
            if (mag > _peak)
                _peak = mag;
            else
                _peak *= _peakRelease;
            float quiet = _peak * 0.2f;
            float gate = 1f;
            if (mag < quiet && quiet > 1e-6f)
            {
                float open = mag / quiet;
                gate = open * open;
            }

            samples[i] = shaped * gate;
        }
    }

    private sealed class Biquad
    {
        private readonly float _b0;
        private readonly float _b1;
        private readonly float _b2;
        private readonly float _a1;
        private readonly float _a2;
        private float _z1;
        private float _z2;

        private Biquad(float b0, float b1, float b2, float a1, float a2)
        {
            _b0 = b0;
            _b1 = b1;
            _b2 = b2;
            _a1 = a1;
            _a2 = a2;
        }

        public static Biquad Highpass(int sampleRate, double frequency) => Design(sampleRate, frequency, highpass: true);

        public static Biquad Lowpass(int sampleRate, double frequency) => Design(sampleRate, frequency, highpass: false);

        public float Process(float sample)
        {
            float y = _b0 * sample + _z1;
            _z1 = _b1 * sample - _a1 * y + _z2;
            _z2 = _b2 * sample - _a2 * y;
            return y;
        }

        private static Biquad Design(int sampleRate, double frequency, bool highpass)
        {
            double w0 = 2 * Math.PI * frequency / sampleRate;
            double cosine = Math.Cos(w0);
            double alpha = Math.Sin(w0) / (2 * 0.707);
            double b0;
            double b1;
            double b2;
            if (highpass)
            {
                b0 = (1 + cosine) / 2;
                b1 = -(1 + cosine);
                b2 = (1 + cosine) / 2;
            }
            else
            {
                b0 = (1 - cosine) / 2;
                b1 = 1 - cosine;
                b2 = (1 - cosine) / 2;
            }

            double a0 = 1 + alpha;
            return new Biquad(
                (float)(b0 / a0),
                (float)(b1 / a0),
                (float)(b2 / a0),
                (float)(-2 * cosine / a0),
                (float)((1 - alpha) / a0));
        }
    }
}
