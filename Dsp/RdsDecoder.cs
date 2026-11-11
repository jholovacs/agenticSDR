namespace AgenticSdr.Dsp;

/// <summary>
/// FM RDS/RBDS decoder. Mixes the 57 kHz data carrier with 3× the stereo pilot (I and Q),
/// wipes residual phase with a BPSK Costas loop, correlates a Manchester matched filter
/// locked to pilot÷16, differentially decodes, and prints PI / name / radio text when stable.
/// </summary>
public sealed class RdsDecoder
{
    private const double NominalBitRate = 1_187.5;
    private const double NominalPilotHz = 19_000;
    private static readonly int[] Offsets = [0x0FC, 0x198, 0x168, 0x350, 0x1B4];

    private readonly double _sampleRate;
    private readonly RealFir _lpI;
    private readonly RealFir _lpQ;

    private double _iEnergy;
    private double _qEnergy;
    private double _costasPhase;

    // Bit phase in [0, 1): advances by (pilotHz/16)/sampleRate each sample.
    private double _bitPhase;
    private float _corr;
    private int _prevDiff = -1;
    private int _retimeCounter;
    private readonly float[] _bbHist;
    private int _bbHistPos;
    private int _bbHistLen;

    private readonly List<int> _bits = [];
    private readonly ushort[] _group = new ushort[4];
    private readonly bool[] _have = new bool[4];
    private int _expect = -1;
    private int _syncScore;
    private int _badStreak;

    private string _ps = "";
    private string _psCandidate = "";
    private int _psConfirm;
    private readonly char[] _psChars = [' ', ' ', ' ', ' ', ' ', ' ', ' ', ' '];
    private readonly int[] _psCharHits = new int[8];
    private readonly bool[] _psHave = new bool[4];
    private readonly char[] _rtChars = new char[64];
    private int _rtAb = -1;
    private string? _pi;
    private string? _callsign;
    private string? _piCandidate;
    private int _piConfirm;
    private string? _lastAnnounce;
    private readonly Queue<string> _pending = [];
    private int _samplesSeen;
    private bool _reportedWaiting;
    private int _crcHits;
    private int _groupsOk;
    private int _bitsEmitted;
    // Session votes for block-A PI across DecodeWindow calls (US RBDS is too weak for 5 hits/window).
    private readonly Dictionary<ushort, int> _piVotes = [];
    private readonly Dictionary<ushort, int> _piWindowWins = [];

    public string DebugSummary =>
        $"sync={_syncScore} crc={_crcHits} groups={_groupsOk} bits={_bitsEmitted} bad={_badStreak} iE={_iEnergy:0.00e+0} qE={_qEnergy:0.00e+0}";

    public RdsDecoder(double sampleRate)
    {
        _sampleRate = sampleRate;
        _lpI = new RealFir(Filters.Lowpass(95, 2_400, sampleRate), 1);
        _lpQ = new RealFir(Filters.Lowpass(95, 2_400, sampleRate), 1);
        // Keep ~2 bit periods of baseband for periodic timing re-acquisition.
        _bbHist = new float[Math.Max(64, (int)(sampleRate / NominalBitRate * 2))];
        Array.Fill(_rtChars, ' ');
    }

    public bool HasText => _pi is not null || _ps.Trim().Length >= 2;

    public IReadOnlyList<string> Drain()
    {
        if (!_reportedWaiting && !HasText && _samplesSeen > (int)(_sampleRate * 10))
        {
            _reportedWaiting = true;
            _pending.Enqueue("Listening for RDS…");
        }

        if (_pending.Count == 0)
            return [];
        var list = _pending.ToArray();
        _pending.Clear();
        return list;
    }

    public void Push(ReadOnlySpan<float> mpx, ReadOnlySpan<float> cos3Pilot, ReadOnlySpan<float> sin3Pilot) =>
        Push(mpx, cos3Pilot, sin3Pilot, NominalPilotHz);

    public void Push(ReadOnlySpan<float> mpx, ReadOnlySpan<float> cos3Pilot, ReadOnlySpan<float> sin3Pilot, double pilotHz)
    {
        int n = Math.Min(mpx.Length, Math.Min(cos3Pilot.Length, sin3Pilot.Length));
        _samplesSeen += n;
        double bitHz = pilotHz > 18_000 && pilotHz < 20_000 ? pilotHz / 16.0 : NominalBitRate;
        double phaseStep = bitHz / _sampleRate;
        for (int i = 0; i < n; i++)
        {
            float iBase = _lpI.Filter(mpx[i] * 2f * cos3Pilot[i]);
            float qBase = _lpQ.Filter(mpx[i] * 2f * sin3Pilot[i]);
            _iEnergy += 0.0002 * (iBase * iBase - _iEnergy);
            _qEnergy += 0.0002 * (qBase * qBase - _qEnergy);

            // Costas wipe recovers Europe (I) or US RBDS (Q) without a hard path pick.
            double c = Math.Cos(_costasPhase);
            double s = Math.Sin(_costasPhase);
            float ii = (float)(iBase * c + qBase * s);
            float qq = (float)(-iBase * s + qBase * c);
            _costasPhase += 0.03 * ii * qq;
            if (_costasPhase > Math.PI)
                _costasPhase -= 2 * Math.PI;
            else if (_costasPhase < -Math.PI)
                _costasPhase += 2 * Math.PI;

            PushBaseband(ii, phaseStep);
        }
    }

    /// <summary>
    /// Offline-style decode over a multiplex window (a few seconds). Uses max-energy Manchester
    /// timing — much more reliable on weak US RBDS than pure streaming bit sync.
    /// </summary>
    public void DecodeWindow(ReadOnlySpan<float> mpx)
    {
        if (mpx.Length < (int)(_sampleRate * 1.5))
            return;

        _samplesSeen += mpx.Length;
        _bits.Clear();
        _prevDiff = -1;
        Resync();
        _badStreak = 0;

        // Use the most recent power-of-two block (RDS is bursty; older audio may be empty).
        int available = mpx.Length;
        int nfft = 1;
        while (nfft * 2 <= available)
            nfft <<= 1;
        int n = nfft;
        int origin = available - n;
        double[] mpxD = new double[n];
        for (int i = 0; i < n; i++)
            mpxD[i] = mpx[origin + i];
        double[] pilot = FftBandpass(mpxD, 18_700, 19_300);
        double[] phase = AnalyticPhase(pilot);
        double freqSum = 0;
        int freqN = 0;
        for (int i = 1; i < n; i++)
        {
            double d = phase[i] - phase[i - 1];
            if (d > Math.PI)
                d -= 2 * Math.PI;
            else if (d < -Math.PI)
                d += 2 * Math.PI;
            double f = d * _sampleRate / (2 * Math.PI);
            if (f is > 18_500 and < 19_500)
            {
                freqSum += f;
                freqN++;
            }
        }

        double freq = freqN > 0 ? freqSum / freqN : NominalPilotHz;
        // Isolate the ~57 kHz RDS subcarrier before mixing — rejects L−R / SCA for better SNR.
        double[] rdsBand = FftBandpass(mpxD, 54_000, 60_000);
        var mixedI = new double[n];
        var mixedQ = new double[n];
        for (int i = 0; i < n; i++)
        {
            double c = Math.Cos(phase[i]);
            double s = Math.Sin(phase[i]);
            double cos3 = 4 * c * c * c - 3 * c;
            double sin3 = 3 * s - 4 * s * s * s;
            mixedI[i] = rdsBand[i] * 2 * cos3;
            mixedQ[i] = rdsBand[i] * 2 * sin3;
        }

        double[] iBase = FftLowpass(mixedI, 2_400);
        double[] qBase = FftLowpass(mixedQ, 2_400);
        var costasBb = new float[n];
        var qBb = new float[n];
        double costas = 0;
        for (int i = 0; i < n; i++)
        {
            double cc = Math.Cos(costas);
            double ss = Math.Sin(costas);
            float ii = (float)(iBase[i] * cc + qBase[i] * ss);
            float qq = (float)(-iBase[i] * ss + qBase[i] * cc);
            costas += 0.05 * ii * qq;
            costasBb[i] = ii;
            // US RBDS rides the quadrature carrier relative to 3×pilot; keep raw Q as a fallback.
            qBb[i] = (float)qBase[i];
        }

        double bitHz = freq / 16.0;
        double sps = _sampleRate / bitHz;
        int iSps = Math.Max(8, (int)Math.Round(sps));
        int half = iSps / 2;
        var mf = new float[iSps];
        for (int k = 0; k < half; k++)
            mf[k] = 1;
        for (int k = half; k < iSps; k++)
            mf[k] = -1;

        int groupsBefore = _groupsOk;
        List<int>? bestBits = null;
        int bestWindowHits = 0;
        ushort bestWindowPi = 0;
        // Costas (EU/generic) and raw Q (US RBDS) — pick the path with the strongest repeated PI.
        foreach (float[] bb in new[] { costasBb, qBb })
        {
            float[] corr = CorrelateSame(bb, mf);
            int nsym = corr.Length / iSps - 2;
            if (nsym < 32)
                continue;

            double bestE = -1;
            int bestOff = 0;
            int step = Math.Max(1, iSps / 64);
            for (int off = 0; off < iSps; off += step)
            {
                double energy = 0;
                for (int sym = 0; sym < nsym; sym++)
                {
                    int at = off + (int)Math.Round(sym * sps);
                    if ((uint)at >= (uint)corr.Length)
                        break;
                    energy += corr[at] * corr[at];
                }

                if (energy > bestE)
                {
                    bestE = energy;
                    bestOff = off;
                }
            }

            var rawBits = new List<int>(nsym);
            int prev = -1;
            for (int sym = 0; sym < nsym; sym++)
            {
                int at = bestOff + (int)Math.Round(sym * sps);
                if ((uint)at >= (uint)corr.Length)
                    break;
                int diffBit = corr[at] >= 0 ? 1 : 0;
                if (prev < 0)
                {
                    prev = diffBit;
                    continue;
                }

                rawBits.Add(diffBit ^ prev);
                prev = diffBit;
            }

            var piHits = new Dictionary<ushort, int>();
            for (int i = 0; i + 26 <= rawBits.Count; i++)
            {
                int word = 0;
                for (int b = 0; b < 26; b++)
                    word = (word << 1) | rawBits[i + b];
                if (!MatchBlock(word, out ushort payload, out int type) || type != 0)
                    continue;
                piHits[payload] = piHits.GetValueOrDefault(payload) + 1;
            }

            int localBest = 0;
            ushort localPi = 0;
            foreach (var kv in piHits)
            {
                if (kv.Value > localBest)
                {
                    localBest = kv.Value;
                    localPi = kv.Key;
                }
            }

            if (localBest > bestWindowHits)
            {
                bestWindowHits = localBest;
                bestWindowPi = localPi;
                bestBits = rawBits;
            }

            _bits.Clear();
            Resync();
            _syncScore = 0;
            foreach (int bit in rawBits)
            {
                _bits.Add(bit);
                _bitsEmitted++;
                while (_bits.Count >= 26)
                    TryBlock();
            }

            if (_groupsOk > groupsBefore)
                break;
        }

        // Credit a window only when one PI repeats (≥2) — random CRC singles stay out of the tally.
        if (bestWindowHits >= 2 && bestBits is not null)
        {
            _piVotes[bestWindowPi] = _piVotes.GetValueOrDefault(bestWindowPi) + bestWindowHits;
            _piWindowWins[bestWindowPi] = _piWindowWins.GetValueOrDefault(bestWindowPi) + 1;
            if (_pi is not null)
                TryPiLockedGroups(bestBits, ushort.Parse(_pi, System.Globalization.NumberStyles.HexNumber));
        }

        int voteBest = 0;
        int voteSecond = 0;
        ushort votePi = 0;
        foreach (var kv in _piVotes)
        {
            if (kv.Value > voteBest)
            {
                voteSecond = voteBest;
                voteBest = kv.Value;
                votePi = kv.Key;
            }
            else if (kv.Value > voteSecond)
                voteSecond = kv.Value;
        }

        // Need repeated wins across windows (not one lucky burst) plus a clear vote majority.
        int wins = _piWindowWins.GetValueOrDefault(votePi);
        string voteText = votePi.ToString("X4");
        bool solid = voteBest >= 6 && wins >= 3 && voteBest >= Math.Max(3, voteSecond * 2);
        if (!solid)
            return;

        if (_pi is null)
        {
            AnnounceStation(votePi);
            if (bestBits is not null)
                TryPiLockedGroups(bestBits, votePi);
            return;
        }

        // Stick with the first lock unless another PI clearly doubles its votes.
        int currentVotes = 0;
        if (ushort.TryParse(_pi, System.Globalization.NumberStyles.HexNumber, null, out ushort cur)
            && _piVotes.TryGetValue(cur, out int cv))
            currentVotes = cv;
        if (voteText != _pi && voteBest >= currentVotes * 2 && voteBest >= 8)
        {
            AnnounceStation(votePi);
            if (bestBits is not null)
                TryPiLockedGroups(bestBits, votePi);
        }
    }

    private void AnnounceStation(ushort pi)
    {
        _pi = pi.ToString("X4");
        if (TryUsCallsign(pi, out string call))
        {
            _callsign = call;
            Announce($"RDS {call}");
        }
        else
        {
            _callsign = null;
            Announce($"RDS PI {_pi}");
        }
    }

    /// <summary>
    /// Once PI is known, hunt block-A words matching that PI and accept following B/C/D CRCs for PS/RT.
    /// Allows a single-bit correction per block; type 0A may skip a broken C (PI repeat).
    /// </summary>
    private void TryPiLockedGroups(List<int> bits, ushort pi)
    {
        for (int i = 0; i + 104 <= bits.Count; i++)
        {
            int wordA = ReadWord26(bits, i);
            if (!MatchBlockEcc(wordA, out ushort payloadA, out int typeA) || typeA != 0 || payloadA != pi)
                continue;

            int wordB = ReadWord26(bits, i + 26);
            if (!MatchBlockEcc(wordB, out ushort payloadB, out int typeB) || typeB != 1)
                continue;

            int groupType = (payloadB >> 12) & 0xF;
            int version = (payloadB >> 11) & 1;
            int wordC = ReadWord26(bits, i + 52);
            int wordD = ReadWord26(bits, i + 78);

            ushort payloadC;
            ushort payloadD;
            if (groupType == 0 && version == 0)
            {
                // 0A: C is a PI repeat — drop it if broken and still take D for the PS letters.
                if (MatchBlockEcc(wordC, out payloadC, out int typeC) && typeC == 2 && payloadC == pi)
                { }
                else
                    payloadC = pi;

                if (!MatchBlockEcc(wordD, out payloadD, out int typeD) || typeD != 3)
                    continue;
            }
            else
            {
                if (!MatchBlockEcc(wordC, out payloadC, out int typeC) || typeC != 2)
                    continue;
                if (!MatchBlockEcc(wordD, out payloadD, out int typeD) || typeD != 3)
                    continue;
            }

            _group[0] = pi;
            _group[1] = payloadB;
            _group[2] = payloadC;
            _group[3] = payloadD;
            _have[0] = _have[1] = _have[2] = _have[3] = true;
            _groupsOk++;
            HandleGroup();
            i += 103;
        }
    }

    private static int ReadWord26(List<int> bits, int start)
    {
        int word = 0;
        for (int b = 0; b < 26; b++)
            word = (word << 1) | bits[start + b];
        return word;
    }

    /// <summary>CRC match, or the same after flipping any one of the 26 bits.</summary>
    private static bool MatchBlockEcc(int word26, out ushort payload, out int blockType)
    {
        if (MatchBlock(word26, out payload, out blockType))
            return true;
        for (int bit = 0; bit < 26; bit++)
        {
            if (MatchBlock(word26 ^ (1 << bit), out payload, out blockType))
                return true;
        }

        payload = 0;
        blockType = -1;
        return false;
    }

    /// <summary>Cross-correlate <paramref name="signal"/> with reversed <paramref name="kernel"/>, 'same' length.</summary>
    private static float[] CorrelateSame(float[] signal, float[] kernel)
    {
        int n = signal.Length;
        int m = kernel.Length;
        var rev = new float[m];
        for (int i = 0; i < m; i++)
            rev[i] = kernel[m - 1 - i];
        int nfft = 1;
        while (nfft < n + m)
            nfft <<= 1;
        var reA = new double[nfft];
        var imA = new double[nfft];
        var reB = new double[nfft];
        var imB = new double[nfft];
        for (int i = 0; i < n; i++)
            reA[i] = signal[i];
        for (int i = 0; i < m; i++)
            reB[i] = rev[i];
        Fft(reA, imA, inverse: false);
        Fft(reB, imB, inverse: false);
        for (int i = 0; i < nfft; i++)
        {
            double r = reA[i] * reB[i] - imA[i] * imB[i];
            double im = reA[i] * imB[i] + imA[i] * reB[i];
            reA[i] = r;
            imA[i] = im;
        }

        Fft(reA, imA, inverse: true);
        // 'same' mode: center the full correlation on the signal length.
        int full = n + m - 1;
        int start = (full - n) / 2;
        var y = new float[n];
        for (int i = 0; i < n; i++)
            y[i] = (float)reA[start + i];
        return y;
    }

    private double[] FftBandpass(double[] x, double loHz, double hiHz)
    {
        int n = x.Length; // power of two
        var re = new double[n];
        var im = new double[n];
        Array.Copy(x, re, n);
        Fft(re, im, inverse: false);
        for (int k = 0; k <= n / 2; k++)
        {
            double f = k * _sampleRate / n;
            if (f < loHz || f > hiHz)
            {
                re[k] = im[k] = 0;
                if (k > 0 && k < n / 2)
                {
                    re[n - k] = 0;
                    im[n - k] = 0;
                }
            }
        }

        Fft(re, im, inverse: true);
        return re;
    }

    private double[] FftLowpass(double[] x, double cutHz)
    {
        int n = x.Length;
        var re = new double[n];
        var im = new double[n];
        Array.Copy(x, re, n);
        Fft(re, im, inverse: false);
        for (int k = 0; k <= n / 2; k++)
        {
            double f = k * _sampleRate / n;
            if (f > cutHz)
            {
                re[k] = im[k] = 0;
                if (k > 0 && k < n / 2)
                {
                    re[n - k] = 0;
                    im[n - k] = 0;
                }
            }
        }

        Fft(re, im, inverse: true);
        return re;
    }

    /// <summary>Unwrapped instantaneous phase of a real tone via FFT Hilbert transform.</summary>
    private static double[] AnalyticPhase(double[] x)
    {
        int n = x.Length;
        var re = new double[n];
        var im = new double[n];
        Array.Copy(x, re, n);
        Fft(re, im, inverse: false);
        for (int k = 1; k < n / 2; k++)
        {
            re[k] *= 2;
            im[k] *= 2;
        }

        for (int k = n / 2 + 1; k < n; k++)
        {
            re[k] = 0;
            im[k] = 0;
        }

        Fft(re, im, inverse: true);
        var phase = new double[n];
        double prev = Math.Atan2(im[0], re[0]);
        phase[0] = prev;
        for (int i = 1; i < n; i++)
        {
            double p = Math.Atan2(im[i], re[i]);
            double d = p - prev;
            if (d > Math.PI)
                d -= 2 * Math.PI;
            else if (d < -Math.PI)
                d += 2 * Math.PI;
            phase[i] = phase[i - 1] + d;
            prev = p;
        }

        return phase;
    }

    /// <summary>In-place radix-2 Cooley–Tukey FFT. Length must be a power of two.</summary>
    private static void Fft(double[] re, double[] im, bool inverse)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;
            j ^= bit;
            if (i >= j)
                continue;
            (re[i], re[j]) = (re[j], re[i]);
            (im[i], im[j]) = (im[j], im[i]);
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = 2 * Math.PI / len * (inverse ? 1 : -1);
            double wlenRe = Math.Cos(ang);
            double wlenIm = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double wRe = 1;
                double wIm = 0;
                for (int j = 0; j < len / 2; j++)
                {
                    int u = i + j;
                    int v = i + j + len / 2;
                    double tRe = wRe * re[v] - wIm * im[v];
                    double tIm = wRe * im[v] + wIm * re[v];
                    re[v] = re[u] - tRe;
                    im[v] = im[u] - tIm;
                    re[u] += tRe;
                    im[u] += tIm;
                    double nwRe = wRe * wlenRe - wIm * wlenIm;
                    wIm = wRe * wlenIm + wIm * wlenRe;
                    wRe = nwRe;
                }
            }
        }

        if (!inverse)
            return;
        for (int i = 0; i < n; i++)
        {
            re[i] /= n;
            im[i] /= n;
        }
    }

    private void PushBaseband(float sample, double phaseStep)
    {
        _bbHist[_bbHistPos] = sample;
        if (++_bbHistPos >= _bbHist.Length)
            _bbHistPos = 0;
        if (_bbHistLen < _bbHist.Length)
            _bbHistLen++;

        // Every ~0.5 s, pick the bit phase that maximizes Manchester energy (offline max-energy timing).
        if (++_retimeCounter >= (int)(_sampleRate * 0.5) && _bbHistLen == _bbHist.Length)
        {
            _retimeCounter = 0;
            RetuneBitPhase(phaseStep);
        }

        if (_bitPhase < 0.5)
            _corr += sample;
        else
            _corr -= sample;

        _bitPhase += phaseStep;
        if (_bitPhase < 1)
            return;

        _bitPhase -= 1;
        float decision = _corr;
        _corr = 0;
        if (Math.Abs(decision) < 1e-12f)
            return;

        int diffBit = decision >= 0 ? 1 : 0;
        if (_prevDiff < 0)
        {
            _prevDiff = diffBit;
            return;
        }

        int data = diffBit ^ _prevDiff;
        _prevDiff = diffBit;
        _bits.Add(data);
        _bitsEmitted++;

        if (_badStreak > 100 && (_badStreak % 50) == 0)
        {
            _bitPhase = 0.25;
            _corr = 0;
            _prevDiff = -1;
            Resync();
            _costasPhase += Math.PI / 2;
        }

        while (_bits.Count >= 26)
            TryBlock();
    }

    private void RetuneBitPhase(double phaseStep)
    {
        // Brute-force a few candidate phases over the history ring; keep the strongest MF energy.
        int sps = Math.Max(8, (int)Math.Round(1.0 / phaseStep));
        int half = sps / 2;
        int symbols = Math.Max(8, _bbHistLen / sps - 1);
        double bestE = -1;
        double bestPhase = _bitPhase;
        for (int trial = 0; trial < 16; trial++)
        {
            double phase0 = trial / 16.0;
            double energy = 0;
            for (int sym = 0; sym < symbols; sym++)
            {
                float corr = 0;
                for (int k = 0; k < sps; k++)
                {
                    int idx = _bbHistPos - _bbHistLen + sym * sps + k;
                    while (idx < 0)
                        idx += _bbHist.Length;
                    idx %= _bbHist.Length;
                    float s = _bbHist[idx];
                    // Phase offset shifts which half is first.
                    double local = (k / (double)sps + phase0) % 1.0;
                    corr += local < 0.5 ? s : -s;
                }

                energy += corr * corr;
            }

            if (energy > bestE)
            {
                bestE = energy;
                bestPhase = phase0;
            }
        }

        _bitPhase = bestPhase;
        _corr = 0;
        _ = half;
    }

    private void TryBlock()
    {
        int word = 0;
        for (int i = 0; i < 26; i++)
            word = (word << 1) | _bits[i];

        if (!MatchBlock(word, out ushort payload, out int type))
        {
            _bits.RemoveAt(0);
            _badStreak++;
            if (_badStreak > 120)
            {
                _syncScore = 0;
                Resync();
                _badStreak = 60;
            }

            return;
        }

        _bits.RemoveRange(0, 26);
        _crcHits++;

        if (_expect < 0)
        {
            if (type != 0)
                return;

            _expect = 1;
            _group[0] = payload;
            _have[0] = true;
            _badStreak = 0;
            return;
        }

        if (type != _expect)
        {
            _badStreak++;
            if (type == 0)
            {
                _expect = 1;
                _group[0] = payload;
                for (int i = 1; i < 4; i++)
                    _have[i] = false;
                _have[0] = true;
            }

            return;
        }

        _badStreak = 0;
        _group[type] = payload;
        _have[type] = true;
        if (type == 3)
        {
            _syncScore = Math.Min(8, _syncScore + 1);
            if (_have[0] && _have[1] && _have[2] && _have[3])
            {
                _groupsOk++;
                HandleGroup();
            }
            for (int i = 0; i < 4; i++)
                _have[i] = false;
            _expect = 0;
            return;
        }

        _expect = type + 1;
    }

    private void Resync()
    {
        _expect = -1;
        for (int i = 0; i < 4; i++)
            _have[i] = false;
    }

    private void HandleGroup()
    {
        string piText = _group[0].ToString("X4");
        if (_piCandidate != piText)
        {
            _piCandidate = piText;
            _piConfirm = 1;
        }
        else
            _piConfirm++;

        // Announce on first good group — RTL-SDR RDS is often too weak for a second confirm.
        if (_pi != piText && _piConfirm >= 1)
            AnnounceStation(_group[0]);

        int groupType = (_group[1] >> 12) & 0xF;
        int version = (_group[1] >> 11) & 1;
        if (groupType == 0)
        {
            int slot = _group[1] & 0x3;
            int offset = slot * 2;
            char c1 = RdsChar(_group[3] >> 8);
            char c2 = RdsChar(_group[3] & 0xFF);
            if (offset <= 6 && IsPrintableNameChar(c1) && IsPrintableNameChar(c2))
            {
                AcceptPsChar(offset, c1);
                AcceptPsChar(offset + 1, c2);
                if (_psCharHits[offset] >= 1 && _psCharHits[offset + 1] >= 1)
                    _psHave[slot] = true;
            }

            MaybeAnnouncePs();
        }
        else if (groupType == 2)
        {
            int ab = (_group[1] >> 4) & 1;
            if (ab != _rtAb)
            {
                _rtAb = ab;
                Array.Fill(_rtChars, ' ');
            }

            if (version == 0)
            {
                int offset = (_group[1] & 0xF) * 4;
                if (offset <= 60)
                {
                    _rtChars[offset] = RdsChar(_group[2] >> 8);
                    _rtChars[offset + 1] = RdsChar(_group[2] & 0xFF);
                    _rtChars[offset + 2] = RdsChar(_group[3] >> 8);
                    _rtChars[offset + 3] = RdsChar(_group[3] & 0xFF);
                }
            }
            else
            {
                int offset = (_group[1] & 0xF) * 2;
                if (offset <= 62)
                {
                    _rtChars[offset] = RdsChar(_group[3] >> 8);
                    _rtChars[offset + 1] = RdsChar(_group[3] & 0xFF);
                }
            }

            string rt = new string(_rtChars).Trim();
            if (rt.Length >= 8 && rt.Count(static c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9') >= 4)
                Announce($"RDS text: {rt}");
        }
    }

    private void AcceptPsChar(int index, char c)
    {
        if (_psChars[index] == c)
            _psCharHits[index] = Math.Min(8, _psCharHits[index] + 1);
        else
        {
            _psChars[index] = c;
            _psCharHits[index] = 1;
        }
    }

    private void MaybeAnnouncePs()
    {
        // Prefer a full 8-char PS; otherwise speak once we have ≥6 stable letters.
        int filled = 0;
        for (int i = 0; i < 8; i++)
        {
            if (_psCharHits[i] >= 1 && _psChars[i] != ' ')
                filled++;
        }

        bool complete = _psHave[0] && _psHave[1] && _psHave[2] && _psHave[3];
        if (!complete && filled < 6)
            return;

        string ps = new string(_psChars);
        string trimmed = ps.Trim();
        if (trimmed.Length < 2)
            return;

        if (ps == _psCandidate)
            _psConfirm++;
        else
        {
            _psCandidate = ps;
            _psConfirm = 1;
        }

        // First solid assembly is enough on weak RBDS.
        if (_psConfirm >= 1 && ps != _ps)
        {
            _ps = ps;
            string who = _callsign ?? (_pi is not null ? $"PI {_pi}" : "station");
            Announce($"RDS {who}: “{trimmed}”");
        }
    }

    private void Announce(string line)
    {
        if (line == _lastAnnounce)
            return;
        _lastAnnounce = line;
        _reportedWaiting = true;
        _pending.Enqueue(line);
        while (_pending.Count > 16)
            _pending.Dequeue();
    }

    private static bool IsPrintableNameChar(char c) =>
        c is >= ' ' and <= '~';

    private static char RdsChar(int value)
    {
        value &= 0xFF;
        return value is >= 32 and <= 126 ? (char)value : ' ';
    }

    /// <summary>
    /// US RBDS four-letter callsign from a PI code (NRSC-4 style). Returns false for
    /// non-US / three-letter / proprietary codes.
    /// </summary>
    public static bool TryUsCallsign(ushort pi, out string callsign)
    {
        // Mapped K/W calls live in 0x1000..0x994F (base 0 = K, 1 = W).
        if (pi < 0x1000 || pi > 0x994F)
        {
            callsign = "";
            return false;
        }

        int val = pi - 0x1000;
        int base26 = 676 * 26;
        int first = val / base26;
        if (first is not (0 or 1))
        {
            callsign = "";
            return false;
        }

        int rest = val % base26;
        int a = rest / 676;
        rest %= 676;
        int b = rest / 26;
        int c = rest % 26;
        callsign = string.Create(4, (first, a, b, c), static (span, s) =>
        {
            span[0] = s.first == 0 ? 'K' : 'W';
            span[1] = (char)('A' + s.a);
            span[2] = (char)('A' + s.b);
            span[3] = (char)('A' + s.c);
        });
        return true;
    }

    public static bool MatchBlock(int word26, out ushort payload, out int blockType) =>
        TryOffsets(word26, out payload, out blockType);

    private static bool TryOffsets(int word26, out ushort payload, out int blockType)
    {
        payload = (ushort)((word26 >> 10) & 0xFFFF);
        int check = word26 & 0x3FF;
        int crc = RdsCrc(payload);
        for (int i = 0; i < Offsets.Length; i++)
        {
            if ((crc ^ Offsets[i]) != check)
                continue;
            blockType = i switch
            {
                3 => 2,
                4 => 3,
                _ => i
            };
            return true;
        }

        blockType = -1;
        return false;
    }

    public static int RdsCrc(int data16)
    {
        int crc = 0;
        for (int i = 15; i >= 0; i--)
        {
            int bit = ((data16 >> i) & 1) ^ ((crc >> 9) & 1);
            crc = (crc << 1) & 0x3FF;
            if (bit != 0)
                crc ^= 0x1B9;
        }

        return crc;
    }

    public static int MakeBlock(ushort data, int offsetIndex) =>
        (data << 10) | (RdsCrc(data) ^ Offsets[offsetIndex]);

    public static void EncodeBits(ReadOnlySpan<int> dataBits, List<int> halfBits)
    {
        int prev = 0;
        foreach (int bit in dataBits)
        {
            int diff = prev ^ bit;
            prev = diff;
            halfBits.Add(diff);
            halfBits.Add(1 - diff);
        }
    }
}
