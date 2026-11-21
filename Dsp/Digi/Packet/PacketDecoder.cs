using System.Text;

namespace AgenticSdr.Dsp;

/// <summary>
/// One callsign and SSID in an AX.25 address field, with digipeater repeated status.
/// </summary>
public sealed record Ax25Address(string Callsign, int Ssid, bool Repeated = false)
{
    public string Format() =>
        Ssid > 0
            ? $"{Callsign}-{Ssid}{(Repeated ? "*" : "")}"
            : $"{Callsign}{(Repeated ? "*" : "")}";

    public override string ToString() => Format();
}

/// <summary>
/// A decoded AX.25 frame: destination, source, digipeater path, control, optional PID, payload, and formatted string.
/// </summary>
public sealed record Ax25Frame(
    Ax25Address Destination,
    Ax25Address Source,
    IReadOnlyList<Ax25Address> Digipeaters,
    byte Control,
    byte? Pid,
    byte[] Payload,
    string Text);

/// <summary>
/// Reads 1200-baud Bell 202 AFSK audio in streaming blocks, recovers clock and bit timing,
/// decodes NRZI and HDLC bit stuffing, checks CRC-16-CCITT FCS, and formats AX.25 / APRS packets.
/// </summary>
public sealed class PacketReader
{
    public const int BaudRate = 1200;
    public const double MarkFrequencyHz = 1200;
    public const double SpaceFrequencyHz = 2200;
    private const int MinFrameBytes = 16;
    private const int MaxFrameBytes = 1024;

    private int _sampleRate;
    private double _samplesPerSymbol;
    private int _windowSize;
    private double _dPhiMark;
    private double _dPhiSpace;
    private float _discFilterAlpha;
    private double _dpllAlpha;

    private float[] _ringIm = [];
    private float[] _ringQm = [];
    private float[] _ringIs = [];
    private float[] _ringQs = [];
    private int _windowIndex;
    private float _sumIm;
    private float _sumQm;
    private float _sumIs;
    private float _sumQs;

    private double _oscPhaseMark;
    private double _oscPhaseSpace;
    private float _smoothedDisc;
    private bool _prevDiscPositive;
    private double _clockPhase;
    private bool _prevSampledMark;

    private int _onesCount;
    private byte _currentByte;
    private int _bitIndex;
    private bool _inFrame;
    private readonly List<byte> _frameBytes = new(256);
    private readonly List<string> _recentDecoded = new();
    private readonly List<byte[]> _recentRaw = new();

    public IReadOnlyList<string> LastDecoded => _recentDecoded;

    /// <summary>Verified AX.25 frames (including FCS bytes) from the most recent <see cref="Push"/> call and earlier in this reader.</summary>
    public IReadOnlyList<byte[]> LastRawFrames => _recentRaw;

    public PacketReader(int initialSampleRate = 48_000)
    {
        ConfigureRate(initialSampleRate);
    }

    private void ConfigureRate(int rate)
    {
        if (rate <= 0)
            rate = 48_000;
        _sampleRate = rate;
        _samplesPerSymbol = (double)rate / BaudRate;
        _windowSize = Math.Max(4, (int)Math.Round(_samplesPerSymbol));
        _dPhiMark = 2 * Math.PI * MarkFrequencyHz / rate;
        _dPhiSpace = 2 * Math.PI * SpaceFrequencyHz / rate;

        // Cutoff ~1400 Hz for post-detection smoothing
        _discFilterAlpha = (float)Math.Clamp(1.0 - Math.Exp(-2 * Math.PI * 1400.0 / rate), 0.05, 0.5);
        _dpllAlpha = 0.125; // Phase step adjustment factor

        _ringIm = new float[_windowSize];
        _ringQm = new float[_windowSize];
        _ringIs = new float[_windowSize];
        _ringQs = new float[_windowSize];
        _windowIndex = 0;
        _sumIm = 0;
        _sumQm = 0;
        _sumIs = 0;
        _sumQs = 0;
        _oscPhaseMark = 0;
        _oscPhaseSpace = 0;
        _smoothedDisc = 0;
        _prevDiscPositive = false;
        _clockPhase = 0;
        _prevSampledMark = false;
        _onesCount = 0;
        _currentByte = 0;
        _bitIndex = 0;
        _inFrame = false;
        _frameBytes.Clear();
    }

    /// <summary>
    /// Processes a block of audio samples and returns any completed AX.25 packets.
    /// </summary>
    public IReadOnlyList<string> Push(ReadOnlySpan<float> audio, int sampleRate)
    {
        if (sampleRate != _sampleRate)
            ConfigureRate(sampleRate);

        if (audio.Length == 0)
            return [];

        var decodedLines = new List<string>();
        double samplePoint = _samplesPerSymbol / 2.0;

        for (int i = 0; i < audio.Length; i++)
        {
            float sample = audio[i];

            // 1. Matched filter local oscillators
            _oscPhaseMark += _dPhiMark;
            if (_oscPhaseMark > Math.PI) _oscPhaseMark -= 2 * Math.PI;
            _oscPhaseSpace += _dPhiSpace;
            if (_oscPhaseSpace > Math.PI) _oscPhaseSpace -= 2 * Math.PI;

            float cosM = (float)Math.Cos(_oscPhaseMark);
            float sinM = (float)Math.Sin(_oscPhaseMark);
            float cosS = (float)Math.Cos(_oscPhaseSpace);
            float sinS = (float)Math.Sin(_oscPhaseSpace);

            float im = sample * cosM;
            float qm = sample * sinM;
            float is_ = sample * cosS;
            float qs = sample * sinS;

            // 2. Sliding window integration
            int idx = _windowIndex;
            _sumIm += im - _ringIm[idx];
            _sumQm += qm - _ringQm[idx];
            _sumIs += is_ - _ringIs[idx];
            _sumQs += qs - _ringQs[idx];

            _ringIm[idx] = im;
            _ringQm[idx] = qm;
            _ringIs[idx] = is_;
            _ringQs[idx] = qs;

            _windowIndex++;
            if (_windowIndex >= _windowSize)
                _windowIndex = 0;

            // 3. Mark/Space energy & smoothed discriminant
            float pMark = _sumIm * _sumIm + _sumQm * _sumQm;
            float pSpace = _sumIs * _sumIs + _sumQs * _sumQs;
            float rawDisc = pMark - pSpace;
            _smoothedDisc += _discFilterAlpha * (rawDisc - _smoothedDisc);

            // 4. Zero crossing detection & DPLL synchronization
            bool discPositive = _smoothedDisc >= 0;
            if (discPositive != _prevDiscPositive)
            {
                // Boundary transition occurred. Ideal clockPhase is 0 (or _samplesPerSymbol).
                double err = _clockPhase < samplePoint
                    ? _clockPhase
                    : _clockPhase - _samplesPerSymbol;

                _clockPhase -= _dpllAlpha * err;
                if (_clockPhase < 0)
                    _clockPhase += _samplesPerSymbol;
                else if (_clockPhase >= _samplesPerSymbol)
                    _clockPhase -= _samplesPerSymbol;

                _prevDiscPositive = discPositive;
            }

            // 5. Symbol sampling at mid-symbol
            double prevClock = _clockPhase;
            _clockPhase += 1.0;

            if (prevClock < samplePoint && _clockPhase >= samplePoint)
            {
                bool mark = _smoothedDisc >= 0;
                // NRZI: transition = 0, no transition = 1
                int bit = (mark == _prevSampledMark) ? 1 : 0;
                _prevSampledMark = mark;

                ProcessBit(bit, decodedLines);
            }

            if (_clockPhase >= _samplesPerSymbol)
                _clockPhase -= _samplesPerSymbol;
        }

        if (decodedLines.Count > 0)
        {
            _recentDecoded.AddRange(decodedLines);
            while (_recentDecoded.Count > 100)
                _recentDecoded.RemoveAt(0);
        }

        return decodedLines;
    }

    private void ProcessBit(int bit, List<string> decodedLines)
    {
        if (bit == 1)
        {
            _onesCount++;
            if (_onesCount == 7)
            {
                // Abort sequence
                _inFrame = false;
                _frameBytes.Clear();
                _currentByte = 0;
                _bitIndex = 0;
                return;
            }

            if (_inFrame)
            {
                _currentByte |= (byte)(1 << _bitIndex);
                _bitIndex++;
                if (_bitIndex == 8)
                {
                    _frameBytes.Add(_currentByte);
                    _currentByte = 0;
                    _bitIndex = 0;
                    if (_frameBytes.Count > MaxFrameBytes)
                    {
                        _inFrame = false;
                        _frameBytes.Clear();
                    }
                }
            }
        }
        else // bit == 0
        {
            if (_onesCount == 5)
            {
                // De-stuffing: discard 0 inserted after five 1s
                _onesCount = 0;
                return;
            }

            if (_onesCount == 6)
            {
                // HDLC Flag 0x7E (01111110)
                _onesCount = 0;
                if (_inFrame && _frameBytes.Count >= MinFrameBytes)
                {
                    byte[] raw = _frameBytes.ToArray();
                    Ax25Frame? frame = PacketDecoder.ParseFrame(raw);
                    if (frame is not null)
                    {
                        _recentRaw.Add(raw);
                        while (_recentRaw.Count > 100)
                            _recentRaw.RemoveAt(0);
                        if (!string.IsNullOrWhiteSpace(frame.Text))
                            decodedLines.Add(frame.Text);
                    }
                }

                _frameBytes.Clear();
                _currentByte = 0;
                _bitIndex = 0;
                _inFrame = true;
                return;
            }

            // Normal 0 bit
            _onesCount = 0;
            if (_inFrame)
            {
                _bitIndex++;
                if (_bitIndex == 8)
                {
                    _frameBytes.Add(_currentByte);
                    _currentByte = 0;
                    _bitIndex = 0;
                    if (_frameBytes.Count > MaxFrameBytes)
                    {
                        _inFrame = false;
                        _frameBytes.Clear();
                    }
                }
            }
        }
    }
}

/// <summary>
/// Decodes and formats AX.25 frames, calculates FCS, and synthesizes Bell 202 AFSK test audio.
/// </summary>
public static class PacketDecoder
{
    /// <summary>
    /// Decodes any AX.25 packet frames found in an audio clip.
    /// </summary>
    public static string Decode(ReadOnlySpan<float> audio, int sampleRate)
    {
        if (audio.Length == 0 || sampleRate < 4_000)
            return "";

        var reader = new PacketReader(sampleRate);
        const int chunkSize = 4096;
        var allLines = new List<string>();

        for (int offset = 0; offset < audio.Length; offset += chunkSize)
        {
            int len = Math.Min(chunkSize, audio.Length - offset);
            IReadOnlyList<string> lines = reader.Push(audio.Slice(offset, len), sampleRate);
            allLines.AddRange(lines);
        }

        return AprsExplain.FormatDecoded(string.Join('\n', allLines));
    }

    /// <summary>
    /// Returns FCS-verified raw AX.25 frames (address through FCS) recovered from Bell 202 audio.
    /// </summary>
    public static IReadOnlyList<byte[]> DecodeRawFrames(ReadOnlySpan<float> audio, int sampleRate)
    {
        if (audio.Length == 0 || sampleRate < 4_000)
            return [];

        var reader = new PacketReader(sampleRate);
        const int chunkSize = 4096;
        for (int offset = 0; offset < audio.Length; offset += chunkSize)
        {
            int len = Math.Min(chunkSize, audio.Length - offset);
            _ = reader.Push(audio.Slice(offset, len), sampleRate);
        }

        return reader.LastRawFrames.ToArray();
    }

    /// <summary>
    /// Computes the 16-bit CRC-16-CCITT / FCS for AX.25 (polynomial 0x8408, initial 0xFFFF, inverted).
    /// </summary>
    public static ushort ComputeFcs(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int bit = 0; bit < 8; bit++)
            {
                if ((crc & 1) != 0)
                    crc = (ushort)((crc >> 1) ^ 0x8408);
                else
                    crc >>= 1;
            }
        }

        return (ushort)~crc;
    }

    /// <summary>
    /// Validates the FCS of an AX.25 frame whose last two bytes are the received FCS.
    /// </summary>
    public static bool VerifyFcs(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 3)
            return false;

        ushort computed = ComputeFcs(frame[..^2]);
        ushort received = (ushort)(frame[^2] | (frame[^1] << 8));
        return computed == received;
    }

    /// <summary>
    /// Parses raw frame bytes into an <see cref="Ax25Frame"/>.
    /// </summary>
    public static Ax25Frame? ParseFrame(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 16 || !VerifyFcs(bytes))
            return null;

        ReadOnlySpan<byte> data = bytes[..^2];
        if (data.Length < 14)
            return null;

        Ax25Address destination = ParseAddress(data[0..7], out _);
        Ax25Address source = ParseAddress(data[7..14], out bool lastAddress);
        var digis = new List<Ax25Address>();
        int offset = 14;

        while (!lastAddress && offset + 7 <= data.Length)
        {
            Ax25Address digi = ParseAddress(data.Slice(offset, 7), out lastAddress);
            digis.Add(digi);
            offset += 7;
        }

        if (offset >= data.Length)
            return null;

        byte control = data[offset++];
        byte? pid = null;
        byte[] payloadBytes = [];

        // Check if unnumbered information (UI) frame: control byte 0x03 or 0x13
        if ((control & 0xEF) == 0x03 && offset < data.Length)
        {
            pid = data[offset++];
            if (offset <= data.Length)
                payloadBytes = data[offset..].ToArray();
        }
        else if (offset <= data.Length)
        {
            payloadBytes = data[offset..].ToArray();
        }

        string payloadText = CleanPayload(payloadBytes);
        string text = FormatFrame(source, destination, digis, control, pid, payloadText);
        return new Ax25Frame(destination, source, digis, control, pid, payloadBytes, text);
    }

    public static Ax25Address ParseAddress(ReadOnlySpan<byte> bytes, out bool isLast)
    {
        var sb = new StringBuilder(6);
        for (int i = 0; i < 6; i++)
        {
            char c = (char)((bytes[i] >> 1) & 0x7F);
            if (c != ' ')
                sb.Append(c);
        }

        byte ssidByte = bytes[6];
        int ssid = (ssidByte >> 1) & 0x0F;
        bool repeated = (ssidByte & 0x80) != 0;
        isLast = (ssidByte & 0x01) != 0;
        return new Ax25Address(sb.ToString(), ssid, repeated);
    }

    public static void EncodeAddress(Span<byte> target, string callsign, int ssid, bool isLast, bool repeated = false)
    {
        string call = (callsign ?? "").Trim().ToUpperInvariant();
        for (int i = 0; i < 6; i++)
        {
            char c = i < call.Length ? call[i] : ' ';
            target[i] = (byte)((c << 1) & 0xFE);
        }

        byte ssidByte = (byte)(0x60 | ((ssid & 0x0F) << 1));
        if (isLast)
            ssidByte |= 0x01;
        if (repeated)
            ssidByte |= 0x80;
        target[6] = ssidByte;
    }

    public static string FormatFrame(
        Ax25Address source,
        Ax25Address destination,
        IReadOnlyList<Ax25Address> digis,
        byte control,
        byte? pid,
        string payloadText)
    {
        var sb = new StringBuilder();
        sb.Append(source.Format());
        sb.Append('>');
        sb.Append(destination.Format());

        if (digis.Count > 0)
        {
            sb.Append(',');
            sb.Append(string.Join(",", digis.Select(d => d.Format())));
        }

        sb.Append(':');
        if (!string.IsNullOrWhiteSpace(payloadText))
            sb.Append(payloadText);
        else
            sb.Append($"[{DescribeControl(control)}]");

        return sb.ToString();
    }

    private static string CleanPayload(byte[] bytes)
    {
        if (bytes.Length == 0)
            return "";

        var sb = new StringBuilder(bytes.Length);
        foreach (byte b in bytes)
        {
            if (b is >= 32 and <= 126)
                sb.Append((char)b);
            else if (b is 10 or 13)
                sb.Append(' ');
        }

        return sb.ToString().Trim();
    }

    private static string DescribeControl(byte control) => control switch
    {
        0x03 or 0x13 => "UI",
        0x2F or 0x3F => "SABM",
        0x43 or 0x53 => "DISC",
        0x63 or 0x73 => "UA",
        0x0F or 0x1F => "DM",
        _ when (control & 0x01) == 0 => "I",
        _ when (control & 0x0F) == 0x01 => "RR",
        _ when (control & 0x0F) == 0x05 => "RNR",
        _ when (control & 0x0F) == 0x09 => "REJ",
        _ => $"0x{control:X2}"
    };

    /// <summary>
    /// Builds raw AX.25 frame bytes (including destination, source, optional digipeaters, control UI 0x03, PID 0xF0, payload, and FCS).
    /// </summary>
    public static byte[] BuildUiFrame(string sourceCall, int sourceSsid, string destCall, int destSsid, string payload, params (string Call, int Ssid, bool Repeated)[] digis)
    {
        int addrCount = 2 + (digis?.Length ?? 0);
        int headerLen = addrCount * 7 + 2; // Addresses + Control (0x03) + PID (0xF0)
        byte[] payloadBytes = Encoding.Latin1.GetBytes(payload ?? "");
        int totalDataLen = headerLen + payloadBytes.Length;
        var data = new byte[totalDataLen + 2]; // + 2 for FCS

        EncodeAddress(data.AsSpan(0, 7), destCall, destSsid, isLast: false);
        bool sourceIsLast = digis is null || digis.Length == 0;
        EncodeAddress(data.AsSpan(7, 7), sourceCall, sourceSsid, isLast: sourceIsLast);

        int offset = 14;
        if (digis is not null)
        {
            for (int i = 0; i < digis.Length; i++)
            {
                bool isLast = i == digis.Length - 1;
                EncodeAddress(data.AsSpan(offset, 7), digis[i].Call, digis[i].Ssid, isLast, digis[i].Repeated);
                offset += 7;
            }
        }

        data[offset++] = 0x03; // UI control
        data[offset++] = 0xF0; // No layer 3 PID
        payloadBytes.CopyTo(data, offset);

        ushort fcs = ComputeFcs(data.AsSpan(0, totalDataLen));
        data[^2] = (byte)(fcs & 0xFF);
        data[^1] = (byte)(fcs >> 8);

        return data;
    }

    /// <summary>Default TX audio / FM baseband rate: integer multiple of 1200 baud (40 samples/bit).</summary>
    public const int TxAudioRate = 48_000;

    /// <summary>Opening HDLC flags before the stuffed frame (~267 ms at 1200 baud) for TNC squelch / DPLL lock.</summary>
    public const int TxPreambleFlags = 40;

    /// <summary>Closing HDLC flags after the FCS so the carrier stays up through the receiver IF.</summary>
    public const int TxPostambleFlags = 5;

    /// <summary>+4.0 dB Space (2200 Hz) vs Mark (1200 Hz) to offset VHF FM de-emphasis.</summary>
    public const double SpacePreEmphasisDb = 4.0;

    /// <summary>Peak FM deviation for 1200-baud packet on VHF (±3.2 kHz).</summary>
    public const double FmDeviationHz = 3_200.0;

    /// <summary>
    /// Synthesizes Bell 202 AFSK audio (1200 baud, continuous-phase 1200/2200 Hz, Space pre-emphasis)
    /// for a UI frame with KPC-3-friendly preamble/postamble.
    /// </summary>
    public static float[] SynthesizeUiAudio(
        string sourceCall,
        int sourceSsid,
        string destCall,
        int destSsid,
        string payload,
        int sampleRate = TxAudioRate,
        int preambleFlags = TxPreambleFlags,
        int postambleFlags = TxPostambleFlags,
        params (string Call, int Ssid, bool Repeated)[] digis)
    {
        byte[] frameBytes = BuildUiFrame(sourceCall, sourceSsid, destCall, destSsid, payload, digis);
        return SynthesizeAfsk(frameBytes, sampleRate, preambleFlags, postambleFlags);
    }

    /// <summary>
    /// Builds the HDLC bit stream: unstuffed preamble/postamble flags, LSB-first payload+FCS with bit-stuffing, then NRZI tones.
    /// </summary>
    public static float[] SynthesizeAfsk(
        ReadOnlySpan<byte> frameBytes,
        int sampleRate = TxAudioRate,
        int preambleFlags = TxPreambleFlags,
        int postambleFlags = TxPostambleFlags)
    {
        if (sampleRate <= 0 || sampleRate % PacketReader.BaudRate != 0)
            throw new ArgumentException($"AFSK sample rate must be a positive multiple of {PacketReader.BaudRate} baud.", nameof(sampleRate));
        if (preambleFlags < 1)
            throw new ArgumentOutOfRangeException(nameof(preambleFlags));
        if (postambleFlags < 1)
            throw new ArgumentOutOfRangeException(nameof(postambleFlags));

        List<int> bits = BuildHdlcBitStream(frameBytes, preambleFlags, postambleFlags);

        // NRZI: 0 toggles Mark↔Space; 1 holds. Start on Mark.
        var mark = new bool[bits.Count];
        bool toneIsMark = true;
        for (int i = 0; i < bits.Count; i++)
        {
            if (bits[i] == 0)
                toneIsMark = !toneIsMark;
            mark[i] = toneIsMark;
        }

        double spaceGain = Math.Pow(10.0, SpacePreEmphasisDb / 20.0);
        double markGain = 1.0;
        double peak = Math.Max(markGain, spaceGain);
        double markAmp = markGain / peak;
        double spaceAmp = spaceGain / peak;

        int samplesPerBit = sampleRate / PacketReader.BaudRate;
        var audio = new float[mark.Length * samplesPerBit];
        double phase = 0;
        int sampleIdx = 0;
        for (int t = 0; t < mark.Length; t++)
        {
            double freq = mark[t] ? PacketReader.MarkFrequencyHz : PacketReader.SpaceFrequencyHz;
            double amp = mark[t] ? markAmp : spaceAmp;
            double dPhi = 2 * Math.PI * freq / sampleRate;
            for (int s = 0; s < samplesPerBit; s++)
            {
                audio[sampleIdx++] = (float)(Math.Sin(phase) * amp);
                phase += dPhi;
                if (phase >= Math.PI)
                    phase -= 2 * Math.PI;
                else if (phase < -Math.PI)
                    phase += 2 * Math.PI;
            }
        }

        return audio;
    }

    /// <summary>
    /// FM-modulates normalized AFSK audio onto complex baseband with continuous phase and peak deviation <see cref="FmDeviationHz"/>.
    /// </summary>
    public static System.Numerics.Complex[] ModulateFmIq(ReadOnlySpan<float> audio, int sampleRate, double deviationHz = FmDeviationHz)
    {
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (deviationHz <= 0)
            throw new ArgumentOutOfRangeException(nameof(deviationHz));

        var iq = new System.Numerics.Complex[audio.Length];
        double phase = 0;
        double step = 2 * Math.PI * (deviationHz / sampleRate);
        for (int n = 0; n < audio.Length; n++)
        {
            phase += step * audio[n];
            if (phase > Math.PI)
                phase -= 2 * Math.PI;
            else if (phase < -Math.PI)
                phase += 2 * Math.PI;
            iq[n] = System.Numerics.Complex.FromPolarCoordinates(1.0, phase);
        }

        return iq;
    }

    /// <summary>
    /// Recovers the FM modulating waveform from complex baseband (for TX→RX round-trip tests).
    /// </summary>
    public static float[] DemodulateFmAudio(ReadOnlySpan<System.Numerics.Complex> iq, int sampleRate, double deviationHz = FmDeviationHz)
    {
        if (iq.Length == 0)
            return [];
        if (sampleRate <= 0 || deviationHz <= 0)
            throw new ArgumentOutOfRangeException(sampleRate <= 0 ? nameof(sampleRate) : nameof(deviationHz));

        var audio = new float[iq.Length];
        double scale = sampleRate / (2 * Math.PI * deviationHz);
        double prev = Math.Atan2(iq[0].Imaginary, iq[0].Real);
        audio[0] = 0;
        for (int n = 1; n < iq.Length; n++)
        {
            double phase = Math.Atan2(iq[n].Imaginary, iq[n].Real);
            double delta = phase - prev;
            prev = phase;
            if (delta > Math.PI)
                delta -= 2 * Math.PI;
            else if (delta < -Math.PI)
                delta += 2 * Math.PI;
            audio[n] = (float)(delta * scale);
        }

        return audio;
    }

    /// <summary>
    /// Upsamples complex baseband to an integer multiple of <paramref name="fromRate"/> with a low-pass anti-imaging FIR
    /// (zero-insert + filter), not zero-order hold or linear interpolation.
    /// </summary>
    public static System.Numerics.Complex[] UpsampleIq(
        ReadOnlySpan<System.Numerics.Complex> input,
        int fromRate,
        int toRate)
    {
        if (fromRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(fromRate));
        if (toRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(toRate));
        if (toRate == fromRate)
            return input.ToArray();
        if (toRate % fromRate != 0)
            throw new ArgumentException($"TX upsample requires an integer rate ratio ({fromRate} → {toRate}).", nameof(toRate));

        int factor = toRate / fromRate;
        int stuffedLen = input.Length * factor;
        var stuffedI = new float[stuffedLen];
        var stuffedQ = new float[stuffedLen];
        for (int i = 0; i < input.Length; i++)
        {
            int at = i * factor;
            // ×factor restores unity passband gain after zero-insertion through a DC-normalized LPF.
            stuffedI[at] = (float)(input[i].Real * factor);
            stuffedQ[at] = (float)(input[i].Imaginary * factor);
        }

        double cutoff = Math.Min(fromRate * 0.45, 12_000);
        float[] taps = Filters.Lowpass(255, cutoff, toRate);
        var firI = new RealFir(taps, 1);
        var firQ = new RealFir(taps, 1);
        var output = new System.Numerics.Complex[stuffedLen];
        for (int i = 0; i < stuffedLen; i++)
            output[i] = new System.Numerics.Complex(firI.Filter(stuffedI[i]), firQ.Filter(stuffedQ[i]));
        return output;
    }

    /// <summary>Interleaves complex samples as I,Q,I,Q… for radio WriteTransmit.</summary>
    public static float[] InterleaveIq(ReadOnlySpan<System.Numerics.Complex> iq)
    {
        var interleaved = new float[iq.Length * 2];
        for (int i = 0; i < iq.Length; i++)
        {
            interleaved[i * 2] = (float)iq[i].Real;
            interleaved[i * 2 + 1] = (float)iq[i].Imaginary;
        }

        return interleaved;
    }

    /// <summary>
    /// Full TX chain: AX.25 UI → AFSK → FM I/Q at <paramref name="audioRate"/>, optionally FIR-upsampled to <paramref name="dacRate"/>.
    /// </summary>
    public static float[] SynthesizeUiIq(
        string sourceCall,
        int sourceSsid,
        string destCall,
        int destSsid,
        string payload,
        int audioRate = TxAudioRate,
        int? dacRate = null,
        params (string Call, int Ssid, bool Repeated)[] digis)
    {
        float[] audio = SynthesizeUiAudio(sourceCall, sourceSsid, destCall, destSsid, payload, audioRate, TxPreambleFlags, TxPostambleFlags, digis);
        System.Numerics.Complex[] iq = ModulateFmIq(audio, audioRate);
        int target = dacRate ?? audioRate;
        if (target != audioRate)
            iq = UpsampleIq(iq, audioRate, target);
        return InterleaveIq(iq);
    }

    static List<int> BuildHdlcBitStream(ReadOnlySpan<byte> frameBytes, int preambleFlags, int postambleFlags)
    {
        var bits = new List<int>((preambleFlags + postambleFlags) * 8 + frameBytes.Length * 10);

        void WriteFlag()
        {
            // 0x7E LSB-first: 0,1,1,1,1,1,1,0 — never bit-stuffed
            bits.Add(0); bits.Add(1); bits.Add(1); bits.Add(1); bits.Add(1); bits.Add(1); bits.Add(1); bits.Add(0);
        }

        for (int i = 0; i < preambleFlags; i++)
            WriteFlag();

        int ones = 0;
        foreach (byte b in frameBytes)
        {
            for (int bit = 0; bit < 8; bit++)
            {
                int val = (b >> bit) & 1;
                bits.Add(val);
                if (val == 1)
                {
                    ones++;
                    if (ones == 5)
                    {
                        bits.Add(0);
                        ones = 0;
                    }
                }
                else
                {
                    ones = 0;
                }
            }
        }

        for (int i = 0; i < postambleFlags; i++)
            WriteFlag();

        return bits;
    }
}
