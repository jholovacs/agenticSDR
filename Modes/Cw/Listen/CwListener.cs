using System.Numerics;
using AgenticSdr.Dsp;
using AgenticSdr.Radio;

namespace AgenticSdr.Cw.Listen;

/// <summary>
/// While the CW desk is not transmitting, tune the open radio, stream demodulated audio
/// like Host <c>/api/live</c>, and decode Morse into the chat.
/// </summary>
public sealed class CwListener : IDisposable
{
    readonly object _gate = new();
    readonly CwChat _chat;
    readonly Func<IRadioDevice?> _radio;
    CancellationTokenSource? _cts;
    Task? _loop;
    CwAudioSink? _audio;
    double _frequencyHz = 14_050_000;
    int _playbackRate = Demodulator.OutputRate;
    bool _wanted;
    bool _transmitting;
    string? _error;

    public CwListener(CwChat chat, Func<IRadioDevice?> radio)
    {
        _chat = chat;
        _radio = radio;
    }

    public ListenStatus Status()
    {
        lock (_gate)
        {
            return new ListenStatus(
                _loop is { IsCompleted: false } && _wanted,
                _frequencyHz,
                _playbackRate,
                _error);
        }
    }

    public sealed record ListenStatus(bool Listening, double FrequencyHz, int SampleRate, string? Error);

    /// <summary>
    /// Attach the HTTP response body so the listen loop can write PCM the same way Host live does.
    /// Completes when the client disconnects, listen stops, or the loop restarts.
    /// </summary>
    public async Task StreamAudioAsync(Stream output, CancellationToken cancellationToken)
    {
        CwAudioSink sink;
        CancellationTokenSource loopCts;
        Task ended;
        lock (_gate)
        {
            if (_audio is null || _loop is null || _loop.IsCompleted || !_wanted || _cts is null)
                throw new InvalidOperationException("Listening has not started yet.");
            sink = _audio;
            loopCts = _cts;
            if (!sink.Attach(output, out ended))
                throw new InvalidOperationException("Listening has not started yet.");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, loopCts.Token);
        try
        {
            await ended.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            /* client left or listen restarted */
        }
        finally
        {
            sink.Detach(output);
        }
    }

    public bool TryAudioRate(out int sampleRate)
    {
        lock (_gate)
        {
            sampleRate = _playbackRate;
            return _audio is not null && _loop is { IsCompleted: false } && _wanted;
        }
    }

    public void SetFrequency(double frequencyHz)
    {
        if (!double.IsFinite(frequencyHz) || frequencyHz <= 0)
            return;
        bool restart;
        lock (_gate)
        {
            if (Math.Abs(_frequencyHz - frequencyHz) < 1)
                return;
            _frequencyHz = frequencyHz;
            restart = _wanted && !_transmitting;
        }

        if (restart)
            Restart();
    }

    public void Start()
    {
        lock (_gate)
        {
            _wanted = true;
            if (_transmitting)
                return;
            // Already pumping — do not tear down the live stream (every POST used to Restart).
            if (_loop is { IsCompleted: false })
                return;
        }

        Restart();
    }

    public void Stop()
    {
        lock (_gate)
        {
            _wanted = false;
            _transmitting = false;
        }

        CancelLoop();
        _chat.SealRx();
    }

    /// <summary>Pause RX while a transmission runs, then call <see cref="EndTransmit"/> when sidetone finishes.</summary>
    public void PauseForTransmit()
    {
        lock (_gate)
            _transmitting = true;
        CancelLoop();
        _chat.SealRx();
    }

    public void EndTransmit()
    {
        bool resume;
        lock (_gate)
        {
            _transmitting = false;
            resume = _wanted;
        }

        if (resume)
            Restart();
    }

    public void Dispose()
    {
        Stop();
    }

    void Restart()
    {
        CancelLoop();
        IRadioDevice? radio = _radio();
        if (radio is null)
        {
            lock (_gate)
                _error = "Open a radio to listen.";
            return;
        }

        double hz;
        lock (_gate)
        {
            if (!_wanted)
                return;
            hz = _frequencyHz;
            _error = null;
            _audio = new CwAudioSink();
            _cts = new CancellationTokenSource();
            CancellationToken token = _cts.Token;
            CwAudioSink sink = _audio;
            _loop = Task.Run(() => RunAsync(radio, hz, sink, token), token);
        }
    }

    void CancelLoop()
    {
        CancellationTokenSource? cts;
        Task? loop;
        CwAudioSink? sink;
        lock (_gate)
        {
            cts = _cts;
            loop = _loop;
            sink = _audio;
            _cts = null;
            _loop = null;
            _audio = null;
        }

        try { cts?.Cancel(); } catch { /* ignore */ }
        try { loop?.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        sink?.Complete();
        cts?.Dispose();
    }

    async Task RunAsync(IRadioDevice device, double stationHz, CwAudioSink sink, CancellationToken ct)
    {
        try
        {
            (double loHz, double shiftHz) = PlaceTuner(device, stationHz);
            device.Tune(loHz);
            FocusTuner(device);
            var junk = new byte[Aligned(device, 32 * 1024)];
            device.Read(junk);

            var demod = new Demodulator(
                RadioMode.Cw,
                shiftHz,
                device.SampleRateHz,
                allowUsbSidebandFlip: device.DirectSampling);

            lock (_gate)
                _playbackRate = demod.PlaybackRate;

            int stride = device.Encoding == SampleEncoding.Cu8 ? 2 : 8;
            int blockBytes = Aligned(device, 262_144);
            // Overlap the next USB read while this block becomes audio — same idea as Host live.
            byte[] bufA = new byte[blockBytes];
            byte[] bufB = new byte[blockBytes];
            byte[] filling = bufA;
            byte[] spare = bufB;
            var recent = new List<float>(demod.PlaybackRate * 8);
            int remember = demod.PlaybackRate * 8;
            var nextDecode = DateTime.UtcNow + TimeSpan.FromSeconds(1);
            Iq.DirectSamplingState? ds = device.DirectSampling ? new Iq.DirectSamplingState() : null;
            Complex[] iqScratch = [];
            float level = 0.05f;
            int decodeBusy = 0;

            Task<int> pending = Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                return device.Read(filling);
            }, ct);

            while (!ct.IsCancellationRequested)
            {
                int n = await pending;
                byte[] filled = filling;
                filling = spare;
                spare = filled;
                byte[] toFill = filling;
                pending = Task.Run(() =>
                {
                    ct.ThrowIfCancellationRequested();
                    return device.Read(toFill);
                }, ct);

                if (n <= 0)
                    throw new RadioException("The radio returned no samples.");

                int usable = n / stride * stride;
                int samples = usable / stride;
                if (iqScratch.Length < samples)
                    iqScratch = new Complex[samples];

                if (device.Encoding == SampleEncoding.Cu8 && !device.DirectSampling)
                    Iq.Write(filled.AsSpan(0, usable), iqScratch.AsSpan(0, samples));
                else if (device.Encoding == SampleEncoding.Cu8 && ds is not null)
                    Iq.Write(filled.AsSpan(0, usable), iqScratch.AsSpan(0, samples), directSampling: true, ds);
                else
                    WriteCf32(filled.AsSpan(0, usable), iqScratch.AsSpan(0, samples));

                demod.Push(iqScratch.AsSpan(0, samples));
                float[] fresh = demod.TakeNewAudio();
                if (fresh.Length == 0)
                    continue;

                level = NextLevel(level, fresh);
                // Host live: write + flush straight to the response body.
                await sink.WriteAsync(ToPcm(fresh, level), ct);

                // Same as Host AppendLimited — per-sample RemoveAt(0) is O(n²) and
                // stalls USB once the 8 s Morse buffer fills (~breakup after a few seconds).
                recent.AddRange(fresh);
                if (recent.Count > remember)
                    recent.RemoveRange(0, recent.Count - remember);

                if (DateTime.UtcNow < nextDecode || recent.Count < demod.PlaybackRate / 4)
                    continue;

                nextDecode = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                if (Interlocked.CompareExchange(ref decodeBusy, 1, 0) != 0)
                    continue;

                float[] snapshot = recent.ToArray();
                int rate = demod.PlaybackRate;
                _ = Task.Run(() =>
                {
                    try
                    {
                        string text = MorseDecoder.DecodeCw(snapshot, rate);
                        if (MorseDecoder.IsCopy(text))
                            _chat.UpdateRx(text);
                    }
                    catch
                    {
                        /* Keep listening even if a decode pass fails. */
                    }
                    finally
                    {
                        Interlocked.Exchange(ref decodeBusy, 0);
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
            /* paused or stopped */
        }
        catch (Exception ex) when (ex is RadioException or InvalidOperationException or IOException or ObjectDisposedException)
        {
            lock (_gate)
                _error = ex.Message;
            _chat.AddNote(ex.Message);
        }
        finally
        {
            ReleaseTuner(device);
            sink.Complete();
            _chat.SealRx();
        }
    }

    static byte[] ToPcm(float[] samples, float level)
    {
        // Use the smoothed level only so gain does not jump every USB block.
        float gain = 0.7f / Math.Max(level, 1e-4f);
        var bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            short value = (short)Math.Clamp(samples[i] * gain * 32767f, -32767, 32767);
            bytes[i * 2] = (byte)value;
            bytes[i * 2 + 1] = (byte)(value >> 8);
        }

        return bytes;
    }

    static float NextLevel(float level, float[] samples)
    {
        float peak = 0;
        foreach (float sample in samples)
            peak = Math.Max(peak, Math.Abs(sample));
        return Math.Max(peak, level * 0.98f);
    }

    static (double LoHz, double ShiftHz) PlaceTuner(IRadioDevice device, double stationHz)
    {
        double offset = Demodulator.TunerOffsetHz(RadioMode.Cw);
        double above = stationHz + offset;
        if (above <= device.Capabilities.MaxFrequencyHz)
            return (above, -offset);
        return (Math.Max(device.Capabilities.MinFrequencyHz, stationHz - offset), offset);
    }

    static void FocusTuner(IRadioDevice device)
    {
        if (device is not RtlSdrDevice rtl || device.DirectSampling)
            return;
        try { rtl.SetBandwidth(Demodulator.TunerBandwidthHz(RadioMode.Cw)); }
        catch (RadioException) { /* digital filter still applies */ }
    }

    static void ReleaseTuner(IRadioDevice device)
    {
        if (device is not RtlSdrDevice rtl || device.DirectSampling)
            return;
        try { rtl.SetBandwidth(0); }
        catch (RadioException) { }
    }

    static int Aligned(IRadioDevice device, int bytes)
    {
        int align = Math.Max(1, device.ReadAlignment);
        if (bytes < align)
            bytes = align;
        return bytes / align * align;
    }

    static void WriteCf32(ReadOnlySpan<byte> bytes, Span<Complex> destination)
    {
        int count = Math.Min(destination.Length, bytes.Length / 8);
        for (int i = 0; i < count; i++)
        {
            float real = BitConverter.ToSingle(bytes.Slice(i * 8, 4));
            float imag = BitConverter.ToSingle(bytes.Slice(i * 8 + 4, 4));
            destination[i] = new Complex(real, imag);
        }
    }
}
