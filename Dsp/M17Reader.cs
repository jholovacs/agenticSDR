using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace AgenticSdr.Dsp;

/// <summary>
/// Receives M17: 4FSK at 4800 symbols/s, open Codec2 speech.
/// Framing uses libm17; speech uses the bundled Codec2 build.
/// </summary>
public sealed class M17Reader : IDisposable
{
    public const int SymbolRate = 4_800;
    public const int BasebandRate = 48_000;
    public const int SamplesPerSymbol = BasebandRate / SymbolRate;
    public const int PayloadSymbols = 184;
    public const int SyncSymbols = 8;
    public const int CodecRate = 8_000;

    private static readonly int[] LsfSync = [+3, +3, +3, +3, -3, -3, +3, -3];
    private static readonly int[] StreamSync = [-3, -3, -3, -3, +3, +3, -3, +3];

    private readonly List<float> _disc = [];
    private readonly List<float> _pcm = [];
    private IntPtr _codec3200 = IntPtr.Zero;
    private IntPtr _codec1600 = IntPtr.Zero;
    private bool _available;
    private bool _announced;
    private string? _src;
    private string? _dst;
    private bool _voice1600;
    private float _prevPhase;
    private bool _havePhase;
    private double _rotator;

    public M17Reader()
    {
        try
        {
            _codec3200 = M17Native.VoiceCreate(3200);
            _codec1600 = M17Native.VoiceCreate(1600);
            _available = _codec3200 != IntPtr.Zero;
        }
        catch (Exception)
        {
            _available = false;
        }
    }

    public bool Available => _available;

    public void Dispose()
    {
        if (_codec3200 != IntPtr.Zero)
        {
            M17Native.VoiceDestroy(_codec3200);
            _codec3200 = IntPtr.Zero;
        }

        if (_codec1600 != IntPtr.Zero)
        {
            M17Native.VoiceDestroy(_codec1600);
            _codec1600 = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Push I/Q. Returns status lines and 8 kHz float PCM when a voice frame decodes.
    /// </summary>
    public List<M17Update> Push(ReadOnlySpan<Complex> iq, double sampleRate, double offsetHz)
    {
        var updates = new List<M17Update>();
        if (!_available || iq.Length == 0 || sampleRate < 16_000)
            return updates;

        AppendDiscriminator(iq, sampleRate, offsetHz);
        if (!_announced)
        {
            _announced = true;
            updates.Add(new M17Update("M17 decoder ready (Codec2).", null));
        }

        while (true)
        {
            if (!TryFindSync(out int syncAt, out bool stream))
                break;

            int need = (syncAt + SyncSymbols + PayloadSymbols) * SamplesPerSymbol;
            if (_disc.Count < need)
                break;

            float[] payload = new float[PayloadSymbols];
            for (int i = 0; i < PayloadSymbols; i++)
                payload[i] = SoftSymbol(syncAt + SyncSymbols + i);

            int drop = (syncAt + SyncSymbols) * SamplesPerSymbol;
            _disc.RemoveRange(0, Math.Min(drop, _disc.Count));

            if (!stream)
                updates.AddRange(HandleLsf(payload));
            else
                updates.AddRange(HandleStream(payload));
        }

        return updates;
    }

    private List<M17Update> HandleLsf(float[] payload)
    {
        var updates = new List<M17Update>();
        var flat = new byte[30];
        try
        {
            M17Native.DecodeLsfBytes(flat, payload);
        }
        catch
        {
            return updates;
        }

        _dst = DecodeCall(flat.AsSpan(0, 6).ToArray());
        _src = DecodeCall(flat.AsSpan(6, 6).ToArray());
        ushort type = (ushort)(flat[12] << 8 | flat[13]);
        // Bits 1..2: 01 data, 10 voice, 11 voice+data (see M17 TYPE).
        int dataType = (type >> 1) & 0b11;
        _voice1600 = dataType == 0b11;
        updates.Add(new M17Update($"M17 link {_src ?? "?"} → {_dst ?? "?"}.", null));
        return updates;
    }

    private List<M17Update> HandleStream(float[] payload)
    {
        var updates = new List<M17Update>();
        var data = new byte[16];
        var lich = new byte[5];
        uint eucl;
        ushort fn;
        try
        {
            eucl = M17Native.DecodeStream(data, lich, out fn, out _, payload);
        }
        catch
        {
            return updates;
        }

        if (eucl > 80)
            return updates;

        IntPtr codec = _voice1600 ? _codec1600 : _codec3200;
        if (codec == IntPtr.Zero)
            return updates;

        int samplesPer = M17Native.VoiceSamplesPerFrame(codec);
        if (samplesPer <= 0)
            return updates;

        int bytesPer = _voice1600 ? 4 : 8;
        var pcm = new short[samplesPer];
        _pcm.Clear();
        for (int offset = 0; offset + bytesPer <= 16; offset += bytesPer)
        {
            var bits = new byte[bytesPer];
            Buffer.BlockCopy(data, offset, bits, 0, bytesPer);
            if (M17Native.VoiceDecode(codec, pcm, bits) <= 0)
                continue;
            for (int i = 0; i < samplesPer; i++)
                _pcm.Add(pcm[i] / 32768f);
        }

        if (_pcm.Count == 0)
            return updates;

        string? note = null;
        if ((fn & 0x7FFF) == 0 && (_src is not null || _dst is not null))
            note = $"M17 voice {_src ?? "?"} → {_dst ?? "?"}.";
        updates.Add(new M17Update(note, _pcm.ToArray()));
        return updates;
    }

    private void AppendDiscriminator(ReadOnlySpan<Complex> iq, double sampleRate, double offsetHz)
    {
        int factor = Math.Max(1, (int)Math.Round(sampleRate / BasebandRate));
        double step = -2 * Math.PI * offsetHz / sampleRate;
        // M17 peak deviation is 2.4 kHz; scale so symbols land near ±1/±3.
        double scale = BasebandRate / (2 * Math.PI * 800.0);
        double accReal = 0;
        double accImag = 0;
        int got = 0;
        for (int i = 0; i < iq.Length; i++)
        {
            _rotator += step;
            if (_rotator > Math.PI)
                _rotator -= 2 * Math.PI;
            else if (_rotator < -Math.PI)
                _rotator += 2 * Math.PI;
            double cosine = Math.Cos(_rotator);
            double sine = Math.Sin(_rotator);
            accReal += iq[i].Real * cosine - iq[i].Imaginary * sine;
            accImag += iq[i].Real * sine + iq[i].Imaginary * cosine;
            got++;
            if (got < factor)
                continue;

            float phase = (float)Math.Atan2(accImag / got, accReal / got);
            accReal = 0;
            accImag = 0;
            got = 0;
            if (!_havePhase)
            {
                _prevPhase = phase;
                _havePhase = true;
                continue;
            }

            float delta = phase - _prevPhase;
            _prevPhase = phase;
            if (delta > MathF.PI)
                delta -= 2 * MathF.PI;
            else if (delta < -MathF.PI)
                delta += 2 * MathF.PI;
            _disc.Add((float)(delta * scale));
        }

        const int keep = BasebandRate * 2;
        if (_disc.Count > keep)
            _disc.RemoveRange(0, _disc.Count - keep);
    }

    private bool TryFindSync(out int syncAt, out bool stream)
    {
        syncAt = 0;
        stream = false;
        int maxStart = _disc.Count / SamplesPerSymbol - SyncSymbols - PayloadSymbols;
        if (maxStart < 1)
            return false;

        double best = 0;
        int bestAt = -1;
        bool bestStream = false;
        int search = Math.Min(maxStart, SymbolRate); // about one second of starts
        for (int start = 0; start < search; start++)
        {
            double lsf = Correlate(start, LsfSync);
            double str = Correlate(start, StreamSync);
            if (lsf > best)
            {
                best = lsf;
                bestAt = start;
                bestStream = false;
            }

            if (str > best)
            {
                best = str;
                bestAt = start;
                bestStream = true;
            }
        }

        if (best < 0.55 || bestAt < 0)
            return false;

        syncAt = bestAt;
        stream = bestStream;
        return true;
    }

    private double Correlate(int symbolIndex, int[] pattern)
    {
        double sum = 0;
        double energy = 0;
        for (int i = 0; i < pattern.Length; i++)
        {
            float sample = SoftSymbol(symbolIndex + i);
            sum += sample * pattern[i];
            energy += sample * sample;
        }

        if (energy < 1e-6)
            return 0;
        return sum / (Math.Sqrt(energy) * Math.Sqrt(pattern.Sum(v => v * v)));
    }

    private float SoftSymbol(int symbolIndex)
    {
        int center = symbolIndex * SamplesPerSymbol + SamplesPerSymbol / 2;
        if (center < 0 || center >= _disc.Count)
            return 0;
        // Average a few mid-symbol samples.
        float sum = 0;
        int n = 0;
        for (int i = -2; i <= 2; i++)
        {
            int at = center + i;
            if (at < 0 || at >= _disc.Count)
                continue;
            sum += _disc[at];
            n++;
        }

        return n == 0 ? 0 : sum / n;
    }

    private static string? DecodeCall(byte[] encoded)
    {
        var buf = new byte[16];
        try
        {
            M17Native.Callsign(buf, encoded);
        }
        catch
        {
            return null;
        }

        string text = Encoding.ASCII.GetString(buf).TrimEnd('\0', ' ');
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}

/// <summary>One M17 decoder tick: an optional status line and optional 8 kHz speech.</summary>
public sealed record M17Update(string? Note, float[]? Pcm8k);
