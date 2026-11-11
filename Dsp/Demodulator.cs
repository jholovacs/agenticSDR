using System.Buffers;
using System.Numerics;

namespace AgenticSdr.Dsp;

/// <summary>
/// Audio produced by a listen, the sample rate of that audio, and decoded text when the mode was CW or FT8.
/// For broadcast FM stereo, <see cref="Audio"/> is interleaved L/R and <see cref="Channels"/> is 2.
/// </summary>
public sealed record DemodResult(float[] Audio, int SampleRate, string? Text, int Channels = 1);

/// <summary>
/// Turns I/Q samples into audio. Narrow FM, AM, and sideband are reduced to about 48 kHz.
/// Wide FM keeps enough bandwidth for a broadcast station, then reduces the audio the same way.
/// CW adds a tone and Morse text. FT8 shifts the upper sideband into the decoder.
/// Push turns each block of I/Q into audio immediately. TakeNewAudio hands that audio to a live player and clears it.
/// Finish is for a completed clip: it keeps every sample, then adds Morse or digimode text.
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
        RadioMode.Ft8 or RadioMode.Js8 or RadioMode.Olivia or RadioMode.Jtty or RadioMode.Varac => 4_200,
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
    private Complex _shiftOsc = Complex.One;
    private readonly Complex _shiftStep;
    private readonly bool _shift;
    private float _prevPhase;
    private bool _hasPhase;
    private float _prevI;
    private float _prevQ;
    private bool _hasIq;
    private float _deemph;
    private float _dcState;
    private float _dcPrev;
    private double _cwPhase;
    private double _audioShiftPhase;
    private double _ft8Phase;
    private bool _ft8Conjugate;
    private bool _ft8OrientationLocked;
    private readonly double _inputRate;
    private readonly int _audioRate;
    private readonly int _decim1;
    private readonly int _decim2;
    private readonly int _ft8OutRate;
    private readonly int _wbfmComplexRate;
    private readonly WbfmMux? _wbfmMux;
    private readonly WbfmProgram _wbfmProgram;
    private readonly RealFir? _mpxDecim;
    private readonly List<float> _mpxScratch = [];
    private readonly List<float> _mpxDecimated = [];

    public Demodulator(RadioMode mode, double offsetHz = 0, double inputRate = InputRate, WbfmProgram wbfmProgram = WbfmProgram.Main)
    {
        _mode = mode == RadioMode.Unknown ? RadioMode.Nbfm : mode;
        _inputRate = inputRate;
        _wbfmProgram = wbfmProgram;
        _shift = offsetHz != 0;
        double dphi = -2 * Math.PI * offsetHz / _inputRate;
        _shiftStep = new Complex(Math.Cos(dphi), Math.Sin(dphi));
        if (_mode == RadioMode.Wbfm)
        {
            (int complexRate, int audioRate, int decim1, int decim2, ComplexDecimator stage, ComplexDecimator channel) = PlanWideFm(inputRate);
            _wbfmComplexRate = complexRate;
            _audioRate = audioRate;
            _decim1 = decim1;
            _decim2 = decim2; // second IQ decimation stage (not audio)
            _stage1 = stage;
            _stage2 = channel;
            int mpxRate = complexRate;
            if (complexRate > 500_000)
            {
                // Discriminator runs wide; mux/RDS run at ~400 kHz after a sharp real decimator.
                _mpxDecim = new RealFir(Filters.Lowpass(127, 180_000, complexRate), 3);
                mpxRate = complexRate / 3;
            }
            else
                _mpxDecim = null;
            _wbfmMux = new WbfmMux(mpxRate, audioRate, wbfmProgram);
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
            _wbfmMux = null;
            _mpxDecim = null;
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
            _wbfmMux = null;
            _mpxDecim = null;
        }

        _ft8OutRate = _audioRate;
        if (_mode is RadioMode.Usb or RadioMode.Lsb)
            _voiceFilter = new ComplexDecimator(Filters.Lowpass(401, 1_550, _audioRate), 1);
        else if (_mode == RadioMode.Cw)
            _voiceFilter = new ComplexDecimator(Filters.Lowpass(127, 800, _audioRate), 1);
        else if (_mode == RadioMode.Varac)
        {
            // VARA HF needs ~2.3 kHz USB; keep 48 kHz for the optional xara/zara monitor.
            _voiceFilter = new ComplexDecimator(Filters.Lowpass(127, 2_800, _audioRate), 1);
        }
        else if (IsUsbDigi(_mode) && _audioRate % Ft8Rate == 0)
        {
            // JS8/FT8 need 12 kHz. Olivia and JTTY also take the same USB window; their decoders resample.
            _ft8Decimator = new ComplexDecimator(Filters.Lowpass(127, 2_100, _audioRate), _audioRate / Ft8Rate);
            _ft8OutRate = Ft8Rate;
        }
    }

    private static bool IsUsbDigi(RadioMode mode) =>
        mode is RadioMode.Ft8 or RadioMode.Js8 or RadioMode.Olivia or RadioMode.Jtty;

    public static DemodResult Demodulate(ReadOnlySpan<Complex> iq, RadioMode mode, double offsetHz = 0, bool normalize = true, double inputRate = InputRate, WbfmProgram wbfmProgram = WbfmProgram.Main)
    {
        var demod = new Demodulator(mode, offsetHz, inputRate, wbfmProgram);
        const int chunk = 240_000;
        for (int offset = 0; offset < iq.Length; offset += chunk)
        {
            int length = Math.Min(chunk, iq.Length - offset);
            demod.Push(iq.Slice(offset, length));
        }

        return demod.Finish(normalize);
    }

    /// <summary>Sample rate of the audio Push has been producing. USB digimodes are 12 kHz. Everything else is about 48 kHz.</summary>
    public int PlaybackRate => IsUsbDigi(_mode) ? _ft8OutRate : _audioRate;

    /// <summary>1 for mono (and SCA). 2 for broadcast-FM main program (interleaved L/R, mono duplicated when unlocked).</summary>
    public int Channels => _wbfmMux?.Channels ?? 1;

    public bool WbfmStereo => _wbfmMux?.Stereo ?? false;
    public bool WbfmSca67 => _wbfmMux?.Sca67 ?? false;
    public bool WbfmSca92 => _wbfmMux?.Sca92 ?? false;
    public WbfmProgram WbfmProgram => _wbfmProgram;

    /// <summary>RDS lines produced since the last drain.</summary>
    public IReadOnlyList<string> TakeRds() => _wbfmMux?.DrainRds() ?? [];

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
            case RadioMode.Js8:
            case RadioMode.Olivia:
            case RadioMode.Jtty:
                DemodFt8(baseBand);
                break;
            case RadioMode.Varac:
                DemodVarac(baseBand);
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
        int rate = IsUsbDigi(_mode) ? _ft8OutRate : _audioRate;
        int channels = Channels;
        string? text = _mode switch
        {
            RadioMode.Cw => MorseDecoder.Decode(audio, rate),
            RadioMode.Ft8 => Ft8Decoder.Format(Ft8Decoder.Decode(audio, rate)),
            RadioMode.Js8 => Js8Decoder.Format(Js8Decoder.Decode(audio, rate)),
            RadioMode.Olivia => OliviaDecoder.Format(OliviaDecoder.Decode(audio, rate)),
            RadioMode.Jtty => JttyDecoder.Format(JttyDecoder.Decode(audio, rate)),
            RadioMode.Varac => FormatVarac(audio, rate),
            RadioMode.Packet => PacketDecoder.Decode(audio, rate),
            RadioMode.Wbfm => FinishRds(_wbfmMux),
            _ => null
        };
        if (normalize)
            Normalize(audio);
        return new DemodResult(audio, rate, text, channels);
    }

    private static string? FinishRds(WbfmMux? mux)
    {
        if (mux is null)
            return null;
        mux.FlushRds();
        IReadOnlyList<string> lines = mux.DrainRds();
        // Drop the waiting placeholder once real RDS arrived; keep a single PI line if several fired.
        var kept = lines.Where(l => !l.StartsWith("Listening for RDS", StringComparison.Ordinal)).ToList();
        if (kept.Count == 0)
        {
            string debug =
                $"RDS debug: {mux.RdsDebugSummary}; pilotMean={mux.PilotMean:0.###} pilotHz={mux.PilotFreq:0.#} stereo={mux.Stereo}";
            return lines.Count > 0 ? string.Join('\n', lines) + "\n" + debug : debug;
        }

        // Keep one station-identity line (callsign or PI); leave name/text lines intact.
        static bool IsIdentity(string l) =>
            l.StartsWith("RDS PI ", StringComparison.Ordinal)
            || (l.StartsWith("RDS ", StringComparison.Ordinal)
                && !l.StartsWith("RDS name", StringComparison.Ordinal)
                && !l.StartsWith("RDS text", StringComparison.Ordinal)
                && !l.StartsWith("RDS debug", StringComparison.Ordinal)
                && !l.Contains(':', StringComparison.Ordinal));

        string? lastId = kept.LastOrDefault(IsIdentity);
        var collapsed = kept.Where(l => !IsIdentity(l)).ToList();
        if (lastId is not null)
            collapsed.Insert(0, lastId);
        return string.Join('\n', collapsed);
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
        // Quadrature discriminator (no atan2): Im(conj(z[n-1])·z[n]) / |z[n-1]|² ≈ Δφ.
        float scale = (float)(_wbfmComplexRate / (2 * Math.PI * deviation));
        _mpxScratch.Clear();
        if (_mpxScratch.Capacity < baseband.Count)
            _mpxScratch.Capacity = baseband.Count;

        foreach (var sample in baseband)
        {
            float i = (float)sample.Real;
            float q = (float)sample.Imaginary;
            if (!_hasIq)
            {
                _prevI = i;
                _prevQ = q;
                _hasIq = true;
                continue;
            }

            float denom = _prevI * _prevI + _prevQ * _prevQ;
            float cross = _prevI * q - _prevQ * i;
            float delta = denom > 1e-20f ? cross / denom : 0f;
            _prevI = i;
            _prevQ = q;
            _mpxScratch.Add(delta * scale);
        }

        ReadOnlySpan<float> mpx = CollectionsMarshalFloat(_mpxScratch);
        if (_mpxDecim is not null)
        {
            _mpxDecimated.Clear();
            _mpxDecim.Process(mpx, _mpxDecimated);
            mpx = CollectionsMarshalFloat(_mpxDecimated);
        }

        MaybeDumpMpx(mpx);
        _wbfmMux!.Process(mpx, _audio);
    }

    private static BinaryWriter? _mpxDump;
    private static void MaybeDumpMpx(ReadOnlySpan<float> mpx)
    {
        string? path = Environment.GetEnvironmentVariable("AGENTIC_DUMP_MPX");
        if (string.IsNullOrWhiteSpace(path) || mpx.Length == 0)
            return;
        try
        {
            _mpxDump ??= new BinaryWriter(File.Open(path, FileMode.Create, FileAccess.Write, FileShare.Read));
            for (int i = 0; i < mpx.Length; i++)
                _mpxDump.Write(mpx[i]);
        }
        catch
        {
            // Diagnosis only — ignore dump failures.
        }
    }

    private static Span<float> CollectionsMarshalFloat(List<float> list) =>
        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list);

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

    private static (int ComplexRate, int AudioRate, int Decim1, int Decim2, ComplexDecimator Stage, ComplexDecimator Channel) PlanWideFm(double inputRate)
    {
        if (inputRate < 220_000)
            throw new ArgumentOutOfRangeException(nameof(inputRate), "Broadcast FM needs a sample rate of at least 220 kHz.");

        // ~480 kHz complex baseband: Nyquist covers ±200 kHz FM, and the live USB reader can keep up.
        // (The old 1.2 MS/s intermediate was too heavy — each overrun is a click.)
        int bestF1 = 0, bestF2 = 0;
        double bestScore = double.MaxValue;
        int maxDecim = Math.Max(1, (int)(inputRate / 320_000));
        for (int f1 = 1; f1 <= maxDecim; f1++)
        {
            for (int f2 = 1; f1 * f2 <= maxDecim; f2++)
            {
                double rate = inputRate / (f1 * f2);
                if (rate is < 400_000 or > 560_000)
                    continue;
                double score = Math.Abs(rate - 480_000) / 1_000 + (f2 > 1 ? 0.5 : 0);
                if (score < bestScore)
                {
                    bestScore = score;
                    bestF1 = f1;
                    bestF2 = f2;
                }
            }
        }

        if (bestF1 == 0)
        {
            bestF1 = Math.Max(1, (int)Math.Round(inputRate / 480_000));
            bestF2 = 1;
            if (inputRate / bestF1 < 320_000)
                throw new ArgumentOutOfRangeException(nameof(inputRate), "No broadcast-FM decimation fits this sample rate.");
        }

        int complexRate = (int)Math.Round(inputRate / (bestF1 * bestF2));
        int audioDecim = (int)Math.Round((double)complexRate / OutputRate);
        int audioRate = complexRate / Math.Max(1, audioDecim);
        double midRate = inputRate / bestF1;
        double midCut = Math.Min(0.45 * midRate, 240_000);
        // Pass pilot / L−R / RDS (≤57 kHz) and the FM deviation skirt; reject the adjacent channel.
        double channelHz = Math.Min(200_000, complexRate * 0.42);
        var stage = new ComplexDecimator(Filters.Lowpass(63, midCut, inputRate), bestF1);
        var channel = new ComplexDecimator(Filters.Lowpass(95, channelHz, midRate), bestF2);
        return (complexRate, audioRate, bestF1, bestF2, stage, channel);
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

    private static string? FormatVarac(float[] audio, int rate) =>
        XaraMonitor.FormatAudio(audio, rate);

    private void DemodVarac(List<Complex> baseband)
    {
        // USB window ~0–3 kHz above the dial for VARA HF, then restore dial-relative pitch.
        // xara expects sound-card audio with the modem around 1.5 kHz, not baseband at DC.
        List<Complex> oriented = ChooseUsbOrientation(baseband);
        var shifted = ShiftAudio(oriented, -1_500, _audioRate);
        var filtered = new List<Complex>(shifted.Count);
        _voiceFilter!.Process(CollectionsMarshal(shifted), filtered);
        double dphi = 2 * Math.PI * 1_500 / _audioRate;
        foreach (var sample in filtered)
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

    private void DemodFt8(List<Complex> baseband)
    {
        // Keep about 100 Hz–4.2 kHz above the dial, then decimate to 12 kHz.
        // Conjugate IQ first when the spectrum is flipped (common on direct-sampling HF paths);
        // pick the orientation that leaves more energy after the USB window filter.
        List<Complex> oriented = ChooseUsbOrientation(baseband);
        var shifted = ShiftAudio(oriented, -2_150, _audioRate);
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

    /// <summary>
    /// USB tones sit above the dial. If I/Q is conjugated they land below and the FT8 window filter
    /// wipes them; conjugating again restores the upper sideband. Orientation is locked after the
    /// first decisive block so noise cannot flip mid-slot and shred Costas.
    /// </summary>
    private List<Complex> ChooseUsbOrientation(List<Complex> baseband)
    {
        if (baseband.Count < 64)
            return baseband;

        if (!_ft8OrientationLocked)
        {
            double plain = UsbOverLsb(baseband, conjugate: false);
            double flipped = UsbOverLsb(baseband, conjugate: true);
            if (flipped > plain * 1.35)
            {
                _ft8Conjugate = true;
                _ft8OrientationLocked = true;
            }
            else if (plain > flipped * 1.35)
            {
                _ft8Conjugate = false;
                _ft8OrientationLocked = true;
            }
            // else keep default (no conjugate) until a later block is decisive
        }

        if (!_ft8Conjugate)
            return baseband;

        var output = new List<Complex>(baseband.Count);
        foreach (Complex sample in baseband)
            output.Add(Complex.Conjugate(sample));
        return output;
    }

    private double UsbOverLsb(List<Complex> baseband, bool conjugate)
    {
        int n = Math.Min(baseband.Count, _audioRate);
        double sumReP = 0, sumImP = 0, sumReN = 0, sumImN = 0;
        for (int i = 0; i < n; i++)
        {
            Complex c = conjugate ? Complex.Conjugate(baseband[i]) : baseband[i];
            double ph = 2 * Math.PI * 1_500 * i / _audioRate;
            double cos = Math.Cos(ph);
            double sin = Math.Sin(ph);
            sumReP += c.Real * cos + c.Imaginary * sin;
            sumImP += c.Imaginary * cos - c.Real * sin;
            sumReN += c.Real * cos - c.Imaginary * sin;
            sumImN += c.Imaginary * cos + c.Real * sin;
        }

        double pPos = sumReP * sumReP + sumImP * sumImP;
        double pNeg = sumReN * sumReN + sumImN * sumImN;
        return pPos / (pNeg + 1e-12);
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
