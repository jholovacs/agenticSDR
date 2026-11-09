using System.Buffers;
using System.Numerics;

namespace AgenticSdr.Dsp;

/// <summary>
/// Audio produced by a listen, the sample rate of that audio, and decoded text when the mode was CW or FT8.
/// </summary>
public sealed record DemodResult(float[] Audio, int SampleRate, string? Text);

/// <summary>
/// Turns I/Q samples into audio. Narrow FM, AM, and sideband are reduced to about 48 kHz.
/// Wide FM keeps enough bandwidth for a broadcast station, then reduces the audio the same way.
/// CW adds a tone and Morse text. FT8 shifts the upper sideband into the decoder.
/// Push turns each block of I/Q into audio immediately. TakeNewAudio hands that audio to a live player and clears it.
/// Finish is for a completed clip: it keeps every sample, then adds Morse or FT8 text.
/// </summary>
public sealed class Demodulator
{
    public const double InputRate = 2_400_000;
    public const int OutputRate = 48_000;
    /// <summary>
    /// How far the tuner sits off a broadcast station. The dongle's own spike is at the tuned frequency,
    /// so the station is placed beside that spike and shifted back before demodulation.
    /// </summary>
    public const double WideFmOffsetHz = 250_000;
    /// <summary>
    /// How far the tuner sits off a voice signal. The dongle's spike stays at the tuned frequency,
    /// outside the voice channel, and the signal is shifted back before demodulation.
    /// </summary>
    public const double VoiceOffsetHz = 50_000;

    /// <summary>How far the tuner sits off the station so the dongle spike stays outside the channel.</summary>
    public static double TunerOffsetHz(RadioMode mode) =>
        mode == RadioMode.Wbfm ? WideFmOffsetHz : VoiceOffsetHz;

    /// <summary>
    /// Audio cutoff after the ×10 and ×5 decimation. The decimation itself stays ×10 then ×5 at 2.4 MS/s.
    /// </summary>
    public static double ChannelCutoffHz(RadioMode mode) => mode switch
    {
        RadioMode.Nbfm or RadioMode.Dmr or RadioMode.M17 or RadioMode.Packet => 8_000,
        RadioMode.Am => 5_000,
        RadioMode.Usb or RadioMode.Lsb => 4_000,
        RadioMode.Cw => 1_200,
        RadioMode.Ft8 => 4_200,
        RadioMode.Raw => 20_000,
        _ => 16_000
    };

    /// <summary>R820T window wide enough to hold the station and the tuner offset.</summary>
    public static int TunerBandwidthHz(RadioMode mode) =>
        mode == RadioMode.Wbfm ? 900_000 : 300_000;
    public const int Ft8Rate = Ft8Decoder.SampleRate;

    private readonly RadioMode _mode;
    private readonly ComplexDecimator _stage1;
    private readonly ComplexDecimator _stage2;
    private readonly ComplexDecimator? _voiceFilter;
    private readonly ComplexDecimator? _ft8Decimator;
    private readonly List<float> _audio = [];
    private readonly List<Complex> _scratchA = new();
    private readonly List<Complex> _scratchB = new();
    private readonly List<Complex> _scratchC = new();
    private readonly List<Complex> _audioIq = new();
    private Complex _shiftOsc = Complex.One;
    private readonly Complex _shiftStep;
    private readonly bool _shift;
    private float _prevPhase;
    private bool _hasPhase;
    private float _deemph;
    private float _dcState;
    private float _dcPrev;
    private double _cwPhase;
    private double _audioShiftPhase;
    private double _ft8Phase;
    private readonly double _inputRate;
    private readonly int _audioRate;
    private readonly int _decim1;
    private readonly int _decim2;
    private readonly int _ft8OutRate;
    private readonly int _wbfmComplexRate;
    private readonly ComplexDecimator? _wbfmAudio;

    public Demodulator(RadioMode mode, double offsetHz = 0, double inputRate = InputRate)
    {
        _mode = mode == RadioMode.Unknown ? RadioMode.Nbfm : mode;
        _inputRate = inputRate;
        _shift = offsetHz != 0;
        double dphi = -2 * Math.PI * offsetHz / _inputRate;
        _shiftStep = new Complex(Math.Cos(dphi), Math.Sin(dphi));
        if (_mode == RadioMode.Wbfm)
        {
            (int complexRate, int audioRate, int decim1, int audioDecim, ComplexDecimator stage, ComplexDecimator channel, ComplexDecimator audio) = PlanWideFm(inputRate);
            _wbfmComplexRate = complexRate;
            _audioRate = audioRate;
            _decim1 = decim1;
            _decim2 = audioDecim;
            _stage1 = stage;
            _stage2 = channel;
            _wbfmAudio = audio;
        }
        else if (Math.Abs(inputRate - InputRate) < 1)
        {
            _audioRate = OutputRate;
            _decim1 = 10;
            _decim2 = 5;
            _stage1 = new ComplexDecimator(Filters.Lowpass(95, 90_000, InputRate), 10);
            double channelHz = ChannelCutoffHz(_mode);
            _stage2 = new ComplexDecimator(Filters.Lowpass(127, channelHz, InputRate / 10), 5);
            _wbfmComplexRate = 0;
            _wbfmAudio = null;
        }
        else
        {
            (int first, int second, int audio) = DemodRates.Plan(inputRate);
            _audioRate = audio;
            _decim1 = first;
            _decim2 = second;
            double midRate = inputRate / first;
            _stage1 = new ComplexDecimator(Filters.Lowpass(95, 0.45 * midRate, inputRate), first);
            double voice = Math.Min(ChannelCutoffHz(_mode), audio * 0.40);
            _stage2 = new ComplexDecimator(Filters.Lowpass(127, voice, midRate), second);
            _wbfmComplexRate = 0;
            _wbfmAudio = null;
        }

        _ft8OutRate = _audioRate;
        if (_mode is RadioMode.Usb or RadioMode.Lsb)
            _voiceFilter = new ComplexDecimator(Filters.Lowpass(401, 1_550, _audioRate), 1);
        else if (_mode == RadioMode.Cw)
            _voiceFilter = new ComplexDecimator(Filters.Lowpass(127, 800, _audioRate), 1);
        else if (_mode == RadioMode.Ft8 && _audioRate % Ft8Rate == 0)
        {
            _ft8Decimator = new ComplexDecimator(Filters.Lowpass(127, 2_100, _audioRate), _audioRate / Ft8Rate);
            _ft8OutRate = Ft8Rate;
        }
    }

    public static DemodResult Demodulate(ReadOnlySpan<Complex> iq, RadioMode mode, double offsetHz = 0, bool normalize = true, double inputRate = InputRate)
    {
        var demod = new Demodulator(mode, offsetHz, inputRate);
        const int chunk = 240_000;
        for (int offset = 0; offset < iq.Length; offset += chunk)
        {
            int length = Math.Min(chunk, iq.Length - offset);
            demod.Push(iq.Slice(offset, length));
        }

        return demod.Finish(normalize);
    }

    /// <summary>Sample rate of the audio Push has been producing. FT8 is 12 kHz. Everything else is about 48 kHz.</summary>
    public int PlaybackRate => _mode == RadioMode.Ft8 ? _ft8OutRate : _audioRate;

    /// <summary>Audio produced since the last take. The samples are removed so a live stream does not keep the whole session.</summary>
    public float[] TakeNewAudio()
    {
        if (_audio.Count == 0)
            return [];
        var copy = _audio.ToArray();
        _audio.Clear();
        return copy;
    }

    public void Push(ReadOnlySpan<Complex> iq)
    {
        if (iq.Length == 0)
            return;

        Complex[]? rented = null;
        ReadOnlySpan<Complex> shifted = iq;
        if (_shift)
        {
            rented = ArrayPool<Complex>.Shared.Rent(iq.Length);
            Rotate(iq, rented);
            shifted = rented.AsSpan(0, iq.Length);
        }

        try
        {
            _scratchA.Clear();
            _stage1.Process(shifted, _scratchA);
            if (_mode == RadioMode.Wbfm)
            {
                _scratchB.Clear();
                _stage2.Process(CollectionsMarshal(_scratchA), _scratchB);
                if (_scratchB.Count > 0)
                    DemodWbfm(_scratchB);
                return;
            }

            _scratchB.Clear();
            _stage2.Process(CollectionsMarshal(_scratchA), _scratchB);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<Complex>.Shared.Return(rented);
        }

        if (_scratchB.Count == 0)
            return;

        List<Complex> baseBand = _scratchB;

        switch (_mode)
        {
            case RadioMode.Am:
                DemodAm(baseBand);
                break;
            case RadioMode.Usb:
                DemodSsb(baseBand, usb: true);
                break;
            case RadioMode.Lsb:
                DemodSsb(baseBand, usb: false);
                break;
            case RadioMode.Cw:
                DemodCw(baseBand);
                break;
            case RadioMode.Ft8:
                DemodFt8(baseBand);
                break;
            case RadioMode.Raw:
            case RadioMode.M17:
                DemodRaw(baseBand);
                break;
            default:
                DemodNbfm(baseBand);
                break;
        }
    }

    public DemodResult Finish(bool normalize)
    {
        var audio = _audio.ToArray();
        RemoveDc(audio);
        int rate = _mode == RadioMode.Ft8 ? _ft8OutRate : _audioRate;
        string? text = _mode switch
        {
            RadioMode.Cw => MorseDecoder.Decode(audio, rate),
            RadioMode.Ft8 => Ft8Decoder.Format(Ft8Decoder.Decode(audio, rate)),
            RadioMode.Packet => PacketDecoder.Decode(audio, rate),
            _ => null
        };
        if (normalize)
            Normalize(audio);
        return new DemodResult(audio, rate, text);
    }

    private void DemodRaw(List<Complex> baseband)
    {
        const double deviation = 12_000;
        double scale = _audioRate / (2 * Math.PI * deviation);
        foreach (var sample in baseband)
        {
            float phase = (float)Math.Atan2(sample.Imaginary, sample.Real);
            if (!_hasPhase)
            {
                _prevPhase = phase;
                _hasPhase = true;
                continue;
            }

            float delta = phase - _prevPhase;
            _prevPhase = phase;
            if (delta > MathF.PI)
                delta -= 2 * MathF.PI;
            else if (delta < -MathF.PI)
                delta += 2 * MathF.PI;
            _audio.Add(Highpass((float)(delta * scale)));
        }
    }

    private void DemodNbfm(List<Complex> baseband)
    {
        const double deviation = 4_000;
        double scale = _audioRate / (2 * Math.PI * deviation);
        double alpha = 1 - Math.Exp(-1.0 / (_audioRate * 75e-6));
        foreach (var sample in baseband)
        {
            float phase = (float)Math.Atan2(sample.Imaginary, sample.Real);
            if (!_hasPhase)
            {
                _prevPhase = phase;
                _hasPhase = true;
                continue;
            }

            float delta = phase - _prevPhase;
            _prevPhase = phase;
            if (delta > MathF.PI)
                delta -= 2 * MathF.PI;
            else if (delta < -MathF.PI)
                delta += 2 * MathF.PI;

            float instant = (float)(delta * scale);
            _deemph += (float)(alpha * (instant - _deemph));
            _audio.Add(Highpass(_deemph));
        }
    }

    private void DemodWbfm(List<Complex> baseband)
    {
        const double deviation = 75_000;
        double scale = _wbfmComplexRate / (2 * Math.PI * deviation);
        double alpha = 1 - Math.Exp(-1.0 / (_wbfmComplexRate * 75e-6));
        _scratchC.Clear();
        foreach (var sample in baseband)
        {
            float phase = (float)Math.Atan2(sample.Imaginary, sample.Real);
            if (!_hasPhase)
            {
                _prevPhase = phase;
                _hasPhase = true;
                continue;
            }

            float delta = phase - _prevPhase;
            _prevPhase = phase;
            if (delta > MathF.PI)
                delta -= 2 * MathF.PI;
            else if (delta < -MathF.PI)
                delta += 2 * MathF.PI;

            float instant = (float)(delta * scale);
            _deemph += (float)(alpha * (instant - _deemph));
            _scratchC.Add(new Complex(_deemph, 0));
        }

        _audioIq.Clear();
        _wbfmAudio!.Process(CollectionsMarshal(_scratchC), _audioIq);
        foreach (var sample in _audioIq)
            _audio.Add(Highpass((float)sample.Real));
    }

    private void Rotate(ReadOnlySpan<Complex> iq, Span<Complex> shifted)
    {
        Complex osc = _shiftOsc;
        Complex step = _shiftStep;
        for (int i = 0; i < iq.Length; i++)
        {
            shifted[i] = iq[i] * osc;
            osc *= step;
            if ((i & 4095) == 4095)
            {
                double inv = 1.0 / osc.Magnitude;
                osc = new Complex(osc.Real * inv, osc.Imaginary * inv);
            }
        }

        _shiftOsc = osc;
    }

    private static (int ComplexRate, int AudioRate, int Decim1, int AudioDecim, ComplexDecimator Stage, ComplexDecimator Channel, ComplexDecimator Audio) PlanWideFm(double inputRate)
    {
        if (inputRate < 220_000)
            throw new ArgumentOutOfRangeException(nameof(inputRate), "Broadcast FM needs a sample rate of at least 220 kHz.");

        int bestFactor = 0;
        double bestScore = double.MaxValue;
        int limit = Math.Max(1, (int)(inputRate / 220_000));
        for (int factor = 1; factor <= limit; factor++)
        {
            double rate = inputRate / factor;
            if (rate < 320_000 || rate > 480_000)
                continue;
            int audioFactor = (int)Math.Round(rate / OutputRate);
            if (audioFactor is < 4 or > 12)
                continue;
            double audio = rate / audioFactor;
            if (audio is < 40_000 or > 56_000)
                continue;
            double score = Math.Abs(audio - OutputRate) * 10 + Math.Abs(rate - 400_000) / 1_000;
            if (score < bestScore)
            {
                bestScore = score;
                bestFactor = factor;
            }
        }

        if (bestFactor == 0)
            throw new ArgumentOutOfRangeException(nameof(inputRate), "No broadcast-FM decimation fits this sample rate.");

        int complexRate = (int)Math.Round(inputRate / bestFactor);
        int audioDecim = (int)Math.Round((double)complexRate / OutputRate);
        int audioRate = complexRate / audioDecim;
        // The sharp channel filter runs after decimation. A 255-tap filter at the full
        // sample rate falls behind the dongle, and each dropped sample is a click.
        double channelHz = Math.Min(110_000, complexRate * 0.22);
        double guardHz = Math.Min(200_000, complexRate - channelHz);
        var stage = new ComplexDecimator(Filters.Lowpass(47, guardHz, inputRate), bestFactor);
        var channel = new ComplexDecimator(Filters.Lowpass(63, channelHz, complexRate), 1);
        var audioFilter = new ComplexDecimator(Filters.Lowpass(127, 15_000, complexRate), audioDecim);
        return (complexRate, audioRate, bestFactor, audioDecim, stage, channel, audioFilter);
    }

    private void DemodAm(List<Complex> baseband)
    {
        foreach (var sample in baseband)
        {
            float mag = (float)sample.Magnitude;
            _audio.Add(Highpass(mag));
        }
    }

    private void DemodSsb(List<Complex> baseband, bool usb)
    {
        double shift = usb ? -1_500 : 1_500;
        var shifted = ShiftAudio(baseband, shift, _audioRate);
        var filtered = new List<Complex>(shifted.Count);
        _voiceFilter!.Process(CollectionsMarshal(shifted), filtered);
        foreach (var sample in filtered)
            _audio.Add((float)sample.Real);
    }

    private void DemodFt8(List<Complex> baseband)
    {
        // Keep about 100 Hz–4.2 kHz above the dial, then decimate to 12 kHz.
        var shifted = ShiftAudio(baseband, -2_150, _audioRate);
        List<Complex> narrow;
        double rate;
        if (_ft8Decimator is null)
        {
            narrow = shifted;
            rate = _audioRate;
        }
        else
        {
            narrow = new List<Complex>(shifted.Count / 4 + 8);
            _ft8Decimator.Process(CollectionsMarshal(shifted), narrow);
            rate = Ft8Rate;
        }

        double dphi = 2 * Math.PI * 2_150 / rate;
        foreach (var sample in narrow)
        {
            _ft8Phase += dphi;
            if (_ft8Phase > Math.PI)
                _ft8Phase -= 2 * Math.PI;
            else if (_ft8Phase < -Math.PI)
                _ft8Phase += 2 * Math.PI;
            Complex turned = sample * new Complex(Math.Cos(_ft8Phase), Math.Sin(_ft8Phase));
            _audio.Add((float)turned.Real);
        }
    }

    private void DemodCw(List<Complex> baseband)
    {
        var filtered = new List<Complex>(baseband.Count);
        _voiceFilter!.Process(CollectionsMarshal(baseband), filtered);
        double dphi = 2 * Math.PI * 700 / _audioRate;
        foreach (var sample in filtered)
        {
            _cwPhase += dphi;
            if (_cwPhase > Math.PI)
                _cwPhase -= 2 * Math.PI;
            float envelope = (float)sample.Magnitude;
            _audio.Add(envelope * (float)Math.Sin(_cwPhase));
        }
    }

    private float Highpass(float sample)
    {
        const float r = 0.995f;
        float y = sample - _dcPrev + r * _dcState;
        _dcPrev = sample;
        _dcState = y;
        return y;
    }

    private List<Complex> ShiftAudio(List<Complex> input, double shiftHz, double sampleRate)
    {
        var output = new List<Complex>(input.Count);
        double dphi = 2 * Math.PI * shiftHz / sampleRate;
        foreach (var sample in input)
        {
            _audioShiftPhase += dphi;
            if (_audioShiftPhase > Math.PI)
                _audioShiftPhase -= 2 * Math.PI;
            else if (_audioShiftPhase < -Math.PI)
                _audioShiftPhase += 2 * Math.PI;
            output.Add(sample * new Complex(Math.Cos(_audioShiftPhase), Math.Sin(_audioShiftPhase)));
        }

        return output;
    }

    private static ReadOnlySpan<Complex> CollectionsMarshal(List<Complex> list) =>
        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list);

    private static void RemoveDc(Span<float> audio)
    {
        if (audio.Length == 0)
            return;
        double mean = 0;
        foreach (float sample in audio)
            mean += sample;
        mean /= audio.Length;
        for (int i = 0; i < audio.Length; i++)
            audio[i] -= (float)mean;
    }

    public static void Normalize(Span<float> audio)
    {
        float peak = 0;
        foreach (float sample in audio)
            peak = Math.Max(peak, Math.Abs(sample));
        if (peak < 1e-4f)
            return;
        float gain = 0.85f / peak;
        for (int i = 0; i < audio.Length; i++)
            audio[i] *= gain;
    }
}
