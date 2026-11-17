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

    public IReadOnlyList<string> LastDecoded => _recentDecoded;

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
                    Ax25Frame? frame = PacketDecoder.ParseFrame(_frameBytes.ToArray());
                    if (frame is not null && !string.IsNullOrWhiteSpace(frame.Text))
                        decodedLines.Add(frame.Text);
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

        return string.Join('\n', allLines);
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

    /// <summary>
    /// Synthesizes Bell 202 AFSK audio (1200 baud, 1200 Hz mark, 2200 Hz space) for a given UI frame.
    /// </summary>
    public static float[] SynthesizeUiAudio(
        string sourceCall,
        int sourceSsid,
        string destCall,
        int destSsid,
        string payload,
        int sampleRate = 48_000,
        int preambleFlags = 20,
        params (string Call, int Ssid, bool Repeated)[] digis)
    {
        byte[] frameBytes = BuildUiFrame(sourceCall, sourceSsid, destCall, destSsid, payload, digis);

        // Convert to bit stream with HDLC bit-stuffing and flags
        var bits = new List<int>();

        // Preamble flags (0x7E = 01111110 LSB first: 0, 1, 1, 1, 1, 1, 1, 0)
        void WriteFlag()
        {
            bits.Add(0); bits.Add(1); bits.Add(1); bits.Add(1); bits.Add(1); bits.Add(1); bits.Add(1); bits.Add(0);
        }

        for (int i = 0; i < preambleFlags; i++)
            WriteFlag();

        // Stuffed frame bits
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
                        bits.Add(0); // Insert stuffed 0
                        ones = 0;
                    }
                }
                else
                {
                    ones = 0;
                }
            }
        }

        // Postamble flags
        for (int i = 0; i < 4; i++)
            WriteFlag();

        // NRZI encoding: 0 = tone transition, 1 = tone hold
        var tones = new List<bool>();
        bool currentTone = true; // start with Mark
        foreach (int bit in bits)
        {
            if (bit == 0)
                currentTone = !currentTone;
            tones.Add(currentTone);
        }

        // Synthesize AFSK audio
        double samplesPerSymbol = (double)sampleRate / PacketReader.BaudRate;
        int totalSamples = (int)Math.Ceiling(tones.Count * samplesPerSymbol);
        var audio = new float[totalSamples];

        double phase = 0;
        int sampleIdx = 0;
        for (int t = 0; t < tones.Count; t++)
        {
            double freq = tones[t] ? PacketReader.MarkFrequencyHz : PacketReader.SpaceFrequencyHz;
            double dPhi = 2 * Math.PI * freq / sampleRate;
            int count = (int)Math.Round((t + 1) * samplesPerSymbol) - (int)Math.Round(t * samplesPerSymbol);

            for (int s = 0; s < count && sampleIdx < totalSamples; s++)
            {
                audio[sampleIdx++] = (float)Math.Sin(phase) * 0.75f;
                phase += dPhi;
                if (phase > 2 * Math.PI)
                    phase -= 2 * Math.PI;
            }
        }

        return audio;
    }
}
