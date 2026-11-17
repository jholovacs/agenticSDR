namespace AgenticSdr.Dsp;

/// <summary>
/// Broadcast-FM multiplex: stereo L/R from the 19 kHz pilot, RDS at 57 kHz, and optional SCA at 67/92 kHz.
/// Input is the raw FM discriminator (pre-emphasis still present). Output is audio at <see cref="AudioRate"/>.
/// Main program is always interleaved stereo (mono is duplicated) so a live stream never changes channel count.
/// </summary>
public sealed class WbfmMux
{
    private const double PilotHz = 19_000;
    private const double PilotLockThreshold = 0.02;
    private const double ScaEnergyThreshold = 0.01;

    private readonly double _mpxRate;
    private readonly int _audioRate;
    private readonly WbfmProgram _program;
    private readonly RealFir _sumLp;
    private readonly RealFir _diffLp;
    private readonly RealFir _scaI;
    private readonly RealFir _scaQ;
    private readonly RealFir _scaAudio;
    private readonly RdsDecoder _rds;
    private readonly float[] _rdsWindow;
    private int _rdsWindowPos;
    private int _rdsWindowLen;
    private int _rdsBatchCounter;
    // 0 = idle, 1 = DecodeWindow running off the audio thread.
    private int _rdsBusy;
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _rdsLines = new();

    // Pilot as a recursive quadrature oscillator (no per-sample Sin/Cos).
    private double _pilotRe = 1;
    private double _pilotIm;
    private double _pilotStepRe = 1;
    private double _pilotStepIm;
    private double _pilotFreq = PilotHz;
    private double _pilotMean;
    private double _pilotPower;
    private int _pilotNormCounter;
    private float _deemphL;
    private float _deemphR;
    private float _scaDeemph;
    private float _scaPrevI;
    private float _scaPrevQ;
    private bool _scaHasPhase;
    private double _scaRe = 1;
    private double _scaIm;
    private double _scaStepRe = 1;
    private double _scaStepIm;
    private double _scaEnergy;
    private double _sca67Energy;
    private double _sca92Energy;
    private double _probe67Re = 1;
    private double _probe67Im;
    private double _probe92Re = 1;
    private double _probe92Im;
    private readonly double _probe67StepRe;
    private readonly double _probe67StepIm;
    private readonly double _probe92StepRe;
    private readonly double _probe92StepIm;
    private float _dcL;
    private float _dcLPrev;
    private float _dcR;
    private float _dcRPrev;
    private int _detectCounter;
    private bool _stereo;
    private bool _sca67;
    private bool _sca92;

    public WbfmMux(double mpxRate, int audioRate, WbfmProgram program = WbfmProgram.Main)
    {
        if (mpxRate < 200_000)
            throw new ArgumentOutOfRangeException(nameof(mpxRate), "Multiplex needs at least 200 kHz.");
        _mpxRate = mpxRate;
        _audioRate = audioRate;
        _program = program;
        int decim = Math.Max(1, (int)Math.Round(mpxRate / audioRate));
        // 63 taps: enough for 15 kHz audio, cheap enough for live USB pacing.
        _sumLp = new RealFir(Filters.Lowpass(63, 15_000, mpxRate), decim);
        _diffLp = new RealFir(Filters.Lowpass(63, 15_000, mpxRate), decim);
        _scaI = new RealFir(Filters.Lowpass(47, 8_000, mpxRate), 1);
        _scaQ = new RealFir(Filters.Lowpass(47, 8_000, mpxRate), 1);
        _scaAudio = new RealFir(Filters.Lowpass(47, 5_000, mpxRate), decim);
        _rds = new RdsDecoder(mpxRate);
        _rdsWindow = new float[Math.Max(16_384, (int)(mpxRate * 3))];
        SetPilotStep(PilotHz);
        double w67 = 2 * Math.PI * 67_000 / mpxRate;
        _probe67StepRe = Math.Cos(w67);
        _probe67StepIm = Math.Sin(w67);
        double w92 = 2 * Math.PI * 92_000 / mpxRate;
        _probe92StepRe = Math.Cos(w92);
        _probe92StepIm = Math.Sin(w92);
        if (program is WbfmProgram.Sca67 or WbfmProgram.Sca92)
        {
            double scaHz = program == WbfmProgram.Sca67 ? 67_000 : 92_000;
            double w = 2 * Math.PI * scaHz / mpxRate;
            _scaStepRe = Math.Cos(w);
            _scaStepIm = Math.Sin(w);
        }
    }

    public int AudioRate => _audioRate;
    public int Channels => _program == WbfmProgram.Main ? 2 : 1;
    public bool Stereo => _stereo;
    public bool Sca67 => _sca67;
    public bool Sca92 => _sca92;

    public IReadOnlyList<string> DrainRds()
    {
        if (_rdsLines.IsEmpty)
            return [];
        var list = new List<string>();
        while (_rdsLines.TryDequeue(out string? line))
            list.Add(line);
        return list;
    }

    public string RdsDebugSummary => _rds.DebugSummary;

    /// <summary>Run one last window decode before a clip finishes.</summary>
    public void FlushRds()
    {
        if (_rdsWindowLen < (int)(_mpxRate * 1.5))
            return;
        if (Math.Abs(_pilotMean) < PilotLockThreshold)
            return;

        var start = Environment.TickCount64;
        while (Interlocked.CompareExchange(ref _rdsBusy, 1, 0) != 0)
        {
            if (Environment.TickCount64 - start > 2_000)
                return;
            Thread.Sleep(1);
        }

        try
        {
            RunRdsWindow(CopyRdsWindow(_rdsWindowPos, _rdsWindowLen));
        }
        finally
        {
            Interlocked.Exchange(ref _rdsBusy, 0);
        }
    }

    public double PilotMean => _pilotMean;
    public double PilotPower => _pilotPower;
    public double PilotFreq => _pilotFreq;

    public void Process(ReadOnlySpan<float> mpx, List<float> audio)
    {
        if (mpx.Length == 0)
            return;

        bool main = _program == WbfmProgram.Main;
        bool sca = _program is WbfmProgram.Sca67 or WbfmProgram.Sca92;
        double alphaDeemph = 1 - Math.Exp(-1.0 / (_audioRate * 75e-6));
        double alphaScaDeemph = 1 - Math.Exp(-1.0 / (_audioRate * 150e-6));
        double pilotLoop = 2 * Math.PI * 12 / _mpxRate;
        double probe67 = 0;
        double probe92 = 0;
        int probeN = 0;

        for (int i = 0; i < mpx.Length; i++)
        {
            float x = mpx[i];

            // Rotate pilot oscillator.
            double pRe = _pilotRe * _pilotStepRe - _pilotIm * _pilotStepIm;
            double pIm = _pilotRe * _pilotStepIm + _pilotIm * _pilotStepRe;
            _pilotRe = pRe;
            _pilotIm = pIm;
            if ((++_pilotNormCounter & 2047) == 0)
            {
                double inv = 1.0 / Math.Sqrt(pRe * pRe + pIm * pIm);
                _pilotRe *= inv;
                _pilotIm *= inv;
                pRe = _pilotRe;
                pIm = _pilotIm;
            }

            // Pilot is a sine: phase error uses cos (re), lock amplitude uses sin (im).
            double err = x * pRe;
            double newFreq = _pilotFreq + pilotLoop * err * 500;
            newFreq = Math.Clamp(newFreq, PilotHz - 80, PilotHz + 80);
            if (Math.Abs(newFreq - _pilotFreq) > 0.05)
            {
                _pilotFreq = newFreq;
                SetPilotStep(newFreq);
            }
            else
                _pilotFreq = newFreq;

            // Proportional phase nudge (small-angle: cos≈1, sin≈θ).
            double nudge = 2.5 * pilotLoop * err;
            if (nudge != 0)
            {
                double r = pRe - pIm * nudge;
                double im = pIm + pRe * nudge;
                _pilotRe = r;
                _pilotIm = im;
                pRe = r;
                pIm = im;
            }

            double lockSample = x * pIm;
            _pilotMean += 0.001 * (lockSample - _pilotMean);
            _pilotPower += 0.001 * (lockSample * lockSample - _pilotPower);

            if (main)
            {
                bool keepSum = _sumLp.Push(x, out float sum);
                // sin(2φ) = 2 sin cos
                float diffMix = (float)(x * 4.0 * pRe * pIm);
                bool keepDiff = _diffLp.Push(diffMix, out float diff);
                if (keepSum && keepDiff)
                {
                    float left = sum + diff;
                    float right = sum - diff;
                    _deemphL += (float)(alphaDeemph * (left - _deemphL));
                    _deemphR += (float)(alphaDeemph * (right - _deemphR));
                    audio.Add(Highpass(_deemphL, ref _dcL, ref _dcLPrev));
                    audio.Add(Highpass(_deemphR, ref _dcR, ref _dcRPrev));
                }
            }
            else if (sca)
            {
                double sRe = _scaRe * _scaStepRe - _scaIm * _scaStepIm;
                double sIm = _scaRe * _scaStepIm + _scaIm * _scaStepRe;
                _scaRe = sRe;
                _scaIm = sIm;
                float iMix = _scaI.Filter((float)(x * sRe));
                float qMix = _scaQ.Filter((float)(x * -sIm));
                float disc = ScaDiscriminate(iMix, qMix);
                if (_scaAudio.Push(disc, out float tone))
                {
                    _scaDeemph += (float)(alphaScaDeemph * (tone - _scaDeemph));
                    audio.Add(Highpass(_scaDeemph, ref _dcL, ref _dcLPrev));
                }

                _scaEnergy += 0.0002 * (iMix * iMix + qMix * qMix - _scaEnergy);
            }

            if ((i & 15) == 0)
            {
                double aRe = _probe67Re * _probe67StepRe - _probe67Im * _probe67StepIm;
                double aIm = _probe67Re * _probe67StepIm + _probe67Im * _probe67StepRe;
                _probe67Re = aRe;
                _probe67Im = aIm;
                probe67 += x * aRe;

                double bRe = _probe92Re * _probe92StepRe - _probe92Im * _probe92StepIm;
                double bIm = _probe92Re * _probe92StepIm + _probe92Im * _probe92StepRe;
                _probe92Re = bRe;
                _probe92Im = bIm;
                probe92 += x * bRe;
                probeN++;
            }
        }

        AppendRdsWindow(mpx);

        _detectCounter += mpx.Length;
        _rdsBatchCounter += mpx.Length;
        // Snapshot indices only — the worker copies the ring so the audio thread never memcpy 3 s of mpx.
        if (_rdsBatchCounter >= (int)(_mpxRate * 3) && _rdsWindowLen == _rdsWindow.Length
            && Math.Abs(_pilotMean) > PilotLockThreshold
            && Interlocked.CompareExchange(ref _rdsBusy, 1, 0) == 0)
        {
            _rdsBatchCounter = 0;
            int pos = _rdsWindowPos;
            int len = _rdsWindowLen;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    RunRdsWindow(CopyRdsWindow(pos, len));
                }
                finally
                {
                    Interlocked.Exchange(ref _rdsBusy, 0);
                }
            });
        }

        if (_detectCounter >= (int)(_mpxRate * 0.25))
        {
            _stereo = Math.Abs(_pilotMean) > PilotLockThreshold && _pilotPower > PilotLockThreshold * PilotLockThreshold * 0.25;
            double e67 = Math.Abs(probe67) / Math.Max(1, probeN);
            double e92 = Math.Abs(probe92) / Math.Max(1, probeN);
            _sca67Energy = 0.5 * _sca67Energy + 0.5 * e67;
            _sca92Energy = 0.5 * _sca92Energy + 0.5 * e92;
            if (_program == WbfmProgram.Sca67)
                _sca67 = _scaEnergy > ScaEnergyThreshold;
            else if (_program == WbfmProgram.Sca92)
                _sca92 = _scaEnergy > ScaEnergyThreshold;
            else
            {
                _sca67 = _sca67Energy > ScaEnergyThreshold * 6;
                _sca92 = _sca92Energy > ScaEnergyThreshold * 6;
            }

            _detectCounter = 0;
        }
    }

    private void SetPilotStep(double freqHz)
    {
        double w = 2 * Math.PI * freqHz / _mpxRate;
        _pilotStepRe = Math.Cos(w);
        _pilotStepIm = Math.Sin(w);
    }

    private void RunRdsWindow(float[] ordered)
    {
        _rds.DecodeWindow(ordered);
        foreach (string line in _rds.Drain())
            _rdsLines.Enqueue(line);
    }

    private void AppendRdsWindow(ReadOnlySpan<float> mpx)
    {
        for (int i = 0; i < mpx.Length; i++)
        {
            _rdsWindow[_rdsWindowPos] = mpx[i];
            if (++_rdsWindowPos >= _rdsWindow.Length)
                _rdsWindowPos = 0;
            if (_rdsWindowLen < _rdsWindow.Length)
                _rdsWindowLen++;
        }
    }

    private float[] CopyRdsWindow(int pos, int len)
    {
        var ordered = new float[len];
        int first = len == _rdsWindow.Length ? pos : 0;
        for (int i = 0; i < len; i++)
            ordered[i] = _rdsWindow[(first + i) % _rdsWindow.Length];
        return ordered;
    }

    private float ScaDiscriminate(float i, float q)
    {
        if (!_scaHasPhase)
        {
            _scaPrevI = i;
            _scaPrevQ = q;
            _scaHasPhase = true;
            return 0;
        }

        float cross = i * _scaPrevQ - q * _scaPrevI;
        float mag = MathF.Sqrt((_scaPrevI * _scaPrevI + _scaPrevQ * _scaPrevQ) * (i * i + q * q));
        _scaPrevI = i;
        _scaPrevQ = q;
        return mag < 1e-9f ? 0 : cross / mag;
    }

    private static float Highpass(float sample, ref float state, ref float prev)
    {
        const float alpha = 0.995f;
        float filtered = alpha * (state + sample - prev);
        prev = sample;
        state = filtered;
        return filtered;
    }
}
