using System.Globalization;
using System.Numerics;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Features;
using AgenticSdr.Dsp;
using AgenticSdr.Privilege;
using AgenticSdr.Shared;
using AgenticSdr.Processors;
using AgenticSdr.Radio;

namespace AgenticSdr.Host;

public sealed partial class RadioSession
{
    public Task WriteLiveAsync(
        double frequencyHz,
        string? modeName,
        DmrFilter dmr,
        HttpResponse response,
        CancellationToken cancellationToken,
        string? wbfmProgram = null,
        IReadOnlyList<string>? pipeline = null) =>
        PlayLiveAsync(frequencyHz, modeName, dmr, response, null, false, WbfmPrograms.Parse(wbfmProgram), pipeline, cancellationToken);

    private Task HoldCampAsync(CampRequest request, CampFanout fanout, CancellationToken cancellationToken) =>
        PlayLiveAsync(request.FrequencyHz, request.Mode, request.Filter, null, fanout, true, WbfmProgram.Main, null, cancellationToken);

    private async Task PlayLiveAsync(
        double frequencyHz,
        string? modeName,
        DmrFilter dmr,
        HttpResponse? response,
        CampFanout? fanout,
        bool squelch,
        WbfmProgram wbfmProgram,
        IReadOnlyList<string>? pipeline,
        CancellationToken cancellationToken)
    {
        RadioMode requested = ModeClassifier.Parse(modeName);
        SetStatus("listening", 0.02, "Waiting for the radio…");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            BeginListen(frequencyHz);
            using CancellationTokenSource linked = Begin(cancellationToken);
            CancellationToken ct = linked.Token;
            IRadioDevice device = RequireDevice();
            RadioMode mode = requested;
            double tuneHz = frequencyHz;
            bool dmrFramed = false;
            SetStatus("listening", 0.05, $"Preparing to listen at {frequencyHz / 1e6:0.000000} MHz…");
            if (requested == RadioMode.Unknown)
            {
                SetStatus("listening", 0.1, "Classifying the signal (this takes a moment)…");
                (mode, tuneHz, _, dmrFramed) = await ResolveAutoModeAsync(device, frequencyHz, ct);
            }

            if (mode is RadioMode.Ft8 or RadioMode.Js8 or RadioMode.Olivia or RadioMode.Jtty or RadioMode.Wspr or RadioMode.Varac)
            {
                if (Region2BandPlan.TryDigiDial(tuneHz, mode, out double dial) || Region2BandPlan.TryDigiDial(frequencyHz, mode, out dial))
                    tuneHz = dial;
                // Leave time to open the stream so live digi audio starts on the UTC boundary.
                if (mode is RadioMode.Ft8)
                    await WaitForDigiSlot(ct, periodSeconds: 15, earlySeconds: 2.0);
                else if (mode is RadioMode.Js8)
                    await WaitForDigiSlot(ct, periodSeconds: 30, earlySeconds: 2.0);
                else if (mode is RadioMode.Wspr)
                    await WaitForDigiSlot(ct, periodSeconds: 120, earlySeconds: 2.0);
            }

            SetStatus("listening", 0.45, "Checking published-channel frequency…");
            tuneHz = await RefinePublishedTuneAsync(device, tuneHz, mode, ct);

            string? source = Region2BandPlan.SourceLabel(tuneHz);
            if (TryLocation(out double latitude, out double longitude) && mode == RadioMode.Nbfm)
            {
                SetStatus("listening", 0.55, "Looking up the repeater name (audio starts after this)…");
                string? call = await repeaters.TryFindAsync(tuneHz, latitude, longitude, ct);
                if (!string.IsNullOrWhiteSpace(call))
                    source = $"{call} ({source})";
            }

            if (mode == RadioMode.Dmr)
                source = "DMR";
            RadioMode demodMode = mode;
            SetStatus("listening", 0.75, $"Tuning {tuneHz / 1e6:0.000000} MHz ({mode})…");
            (double loHz, double shiftHz) = PlaceTuner(device, demodMode, tuneHz);
            EaseWideFmGain(device, demodMode);
            var demod = new Demodulator(
                demodMode,
                shiftHz,
                device.SampleRateHz,
                demodMode == RadioMode.Wbfm ? wbfmProgram : WbfmProgram.Main,
                allowUsbSidebandFlip: device.DirectSampling,
                dopplerMaxHz: Region2BandPlan.IssDopplerMaxHz(tuneHz));
            if (response is not null)
            {
                SetStatus("listening", 0.9, "Starting live audio…");
                response.Headers["X-Audio-Rate"] = demod.PlaybackRate.ToString(CultureInfo.InvariantCulture);
                response.Headers["X-Audio-Channels"] = demod.Channels.ToString(CultureInfo.InvariantCulture);
                response.Headers["X-Tune-Hz"] = tuneHz.ToString(CultureInfo.InvariantCulture);
                response.Headers["X-Mode"] = mode.ToString();
                response.Headers["X-Source"] = source ?? "";
                response.Headers["X-Wbfm-Program"] = WbfmPrograms.WireName(demod.WbfmProgram);
                response.ContentType = "application/octet-stream";
                response.Headers.CacheControl = "no-store";
                response.HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
                await response.StartAsync(ct);
            }

            if (squelch)
            {
                lock (_statusGate)
                {
                    _campSampleRate = demod.PlaybackRate;
                    _campChannels = demod.Channels;
                    _campActivity = false;
                    _campSnrDb = null;
                    string heard = CampSchedule.Canonical(mode);
                    _campTune = (_campTune ?? new CampTune(null, null, tuneHz, heard, false, demod.PlaybackRate))
                        with { FrequencyHz = tuneHz, Mode = heard, Activity = false, SampleRate = demod.PlaybackRate };
                }
            }

            SetStatus("listening", 1, squelch
                ? $"Camped on {tuneHz / 1e6:0.000000} MHz ({mode}), waiting for activity."
                : LiveMessage(mode, tuneHz));
            if (dmrFramed)
                AppendNote("DMR framing found.");
            // USB/LSB on a digi watering hole will play tones but never decode text.
            if (mode is RadioMode.Usb or RadioMode.Lsb
                && Region2BandPlan.TryAnyDigiDial(tuneHz, out RadioMode dialMode, out _))
            {
                AppendNote(
                    $"This frequency is a published {dialMode} dial. Choose {dialMode} in Mode (or Known channels) to decode text — {mode} only plays the audio.");
            }

            SignalChain? chain = null;
            SignalChain? sideOlivia = null;
            try
            {
                chain = OpenPipeline(mode, pipeline);
                // USB on an Olivia dial: decode on a second off-thread chain so voice Morse/speech stay intact.
                if (mode == RadioMode.Usb
                    && Region2BandPlan.TryPublishedStation(tuneHz, RadioMode.Olivia, out _, out _))
                {
                    sideOlivia = OpenPipeline(RadioMode.Olivia, null);
                    AppendNote("Olivia dial on USB — decoding in the background. Mode Olivia uses a wider USB window.");
                }

                await PumpLiveAsync(
                    device, loHz, shiftHz, tuneHz, mode, demod, response?.Body, fanout, squelch,
                    mode == RadioMode.Dmr && !dmrFramed, dmr, chain, sideOlivia, ct);
            }
            finally
            {
                sideOlivia?.Dispose();
                chain?.Dispose();
                ClearPipeline();
                if (EasedGain(demodMode))
                    RestoreGain(device);
                ReleaseTuner(device);
            }

            SetStatus("idle", 0, "Listen stopped.");
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            SetStatus("idle", 0, "Listen stopped.");
        }
        catch (Exception ex) when (ex is RadioException or InvalidOperationException)
        {
            lock (_statusGate)
            {
                _error = ex.Message;
                _phase = "idle";
                _progress = 0;
                _message = ex.Message;
            }

            if (response is { HasStarted: false })
                throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private SignalChain? OpenPipeline(RadioMode mode, IReadOnlyList<string>? overrideIds)
    {
        try
        {
            SignalChain? chain = overrideIds is { Count: > 0 }
                ? processors.CreateChain(overrideIds, mode)
                : processors.TryCreateDefault(mode);
            lock (_statusGate)
                _pipeline = chain?.Stages.ToArray() ?? [];
            return chain;
        }
        catch (Exception ex)
        {
            AppendNote("Pipeline was not started. " + ex.Message);
            lock (_statusGate)
                _pipeline = [];
            return null;
        }
    }

    private void ClearPipeline()
    {
        lock (_statusGate)
            _pipeline = [];
    }

    private void PublishPipelineTexts(SignalChain chain, RadioMode mode, ref bool digiHeader)
    {
        foreach (AttributedText text in chain.DrainTexts())
            ApplyAttributed(text, mode, ref digiHeader);
    }

    private void ApplyAttributed(AttributedText text, RadioMode mode, ref bool digiHeader)
    {
        if (string.IsNullOrWhiteSpace(text.Text))
            return;
        if (text.Kind == "morse")
        {
            AppendMorse(text.Text);
            return;
        }

        if (text.Kind is "ft8" or "js8" or "olivia" or "jtty" or "wspr" or "varac")
        {
            if (text.Kind is "olivia" or "jtty" or "wspr")
            {
                string body = DigiMessageBody(text.Text);
                if (string.IsNullOrWhiteSpace(body))
                    return;
                if (body.Equals(_lastLiveDigiBody, StringComparison.Ordinal))
                    return;
                if (_lastLiveDigiBody.Length > 0
                    && body.Length < _lastLiveDigiBody.Length
                    && _lastLiveDigiBody.Contains(body, StringComparison.Ordinal))
                    return;
                _lastLiveDigiBody = body;
            }

            RadioMode digiMode = text.Kind switch
            {
                "olivia" => RadioMode.Olivia,
                "jtty" => RadioMode.Jtty,
                "wspr" => RadioMode.Wspr,
                "js8" => RadioMode.Js8,
                "varac" => RadioMode.Varac,
                _ => mode
            };
            AppendDigiLines(digiMode, text.Text, ref digiHeader);
            return;
        }

        AppendSpan(text.Kind, text.Text, text.SourceName);
    }

    private async Task PumpLiveAsync(
        IRadioDevice device,
        double frequencyHz,
        double dmrOffsetHz,
        double stationHz,
        RadioMode mode,
        Demodulator demod,
        Stream? output,
        CampFanout? fanout,
        bool squelch,
        bool recheckDmr,
        DmrFilter dmr,
        SignalChain? chain,
        SignalChain? sideOlivia,
        CancellationToken cancellationToken)
    {
        int stride = device.Encoding == SampleEncoding.Cu8 ? 2 : 8;
        int blockBytes = Aligned(device, 262_144);
        var bufA = new byte[blockBytes];
        var bufB = new byte[blockBytes];
        var recent = new List<float>();
        var dmrProbe = new List<Complex>();
        var dmrReader = new DmrReader();
        using var m17Reader = mode == RadioMode.M17 ? new M17Reader() : null;
        if (mode == RadioMode.M17 && m17Reader is { Available: false })
            AppendNote("M17 voice needs Host/native/m17voice.dll beside the app.");
        bool dmrChecked = !recheckDmr;
        int dmrNeed = (int)(device.SampleRateHz * 0.12);
        var digiPeriod = new List<float>();
        var sideOliviaPeriod = new List<float>();
        bool voice = SpeechDecoder.IsVoice(mode);
        bool digi = mode is RadioMode.Ft8 or RadioMode.Js8 or RadioMode.Olivia or RadioMode.Jtty
            or RadioMode.Wspr or RadioMode.Varac;
        bool slidingDigi = mode is RadioMode.Olivia or RadioMode.Jtty;
        bool sideband = mode is RadioMode.Usb or RadioMode.Lsb;
        bool digiHeader = false;
        int rate = demod.PlaybackRate;
        int remember = voice ? 6 * rate : 8 * rate;
        var nextMorse = DateTime.UtcNow + TimeSpan.FromSeconds(1);
        // Live FT8 chops on 15 s; JS8 on 30 s; Olivia/JTTY slide every DigiPeriodSeconds on a longer buffer.
        int digiStep = DigiPeriodSeconds(mode) * rate;
        int digiKeep = DigiListenSeconds(mode) * rate;
        int digiSinceSubmit = 0;
        int sideOliviaStep = DigiPeriodSeconds(RadioMode.Olivia) * rate;
        int sideOliviaKeep = DigiListenSeconds(RadioMode.Olivia) * rate;
        int sideOliviaSince = 0;
        var nextText = DateTime.UtcNow + TimeSpan.FromSeconds(1);
        float level = 0.05f;
        var activity = new ActivityHold();
        bool stereoNoted = false;
        bool sca67Noted = false;
        bool sca92Noted = false;

        // Packet / Ribbit / VarAC stay off the audio pump (dedicated decode thread).
        PacketReader? packetState = mode == RadioMode.Packet ? new PacketReader(rate) : null;
        RibbitReader? ribbitState = mode is RadioMode.Ribbit or RadioMode.Nbfm or RadioMode.Packet
            ? new RibbitReader(rate)
            : null;
        XaraMonitor? xaraState = mode == RadioMode.Varac ? new XaraMonitor() : null;
        if (mode == RadioMode.Varac && xaraState is { Available: false })
            AppendNote(xaraState.MissingHint ?? "VarAC decode needs xara-monitor or zara-monitor beside the app.");

        LiveDecodeWorker? packetWorker = packetState is null
            ? null
            : new LiveDecodeWorker("sdr-packet", (block, ct, emit) =>
            {
                ct.ThrowIfCancellationRequested();
                foreach (string packet in packetState.Push(block, rate))
                    emit("packet", packet);
            });
        LiveDecodeWorker? ribbitWorker = ribbitState is null
            ? null
            : new LiveDecodeWorker("sdr-ribbit", (block, ct, emit) =>
            {
                ct.ThrowIfCancellationRequested();
                foreach (string line in ribbitState.Push(block, rate))
                    emit("ribbit", line);
            });
        LiveDecodeWorker? varacWorker = mode == RadioMode.Varac && xaraState is { Available: true }
            ? new LiveDecodeWorker("sdr-varac", (block, ct, emit) =>
            {
                ct.ThrowIfCancellationRequested();
                xaraState.Push(block, rate);
                IReadOnlyList<XaraMessage> heard = xaraState.Drain();
                if (heard.Count > 0)
                    emit("varac", XaraMonitor.Format(heard));
            })
            : null;

        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            device.Tune(frequencyHz);
            lock (_statusGate)
                _directSampling = device.DirectSampling;
            FocusTuner(device, mode);
            var junk = new byte[Aligned(device, 32 * 1024)];
            device.Read(junk);
            // Align after junk so the first Costas symbols are not clipped by tuner settle.
            if (mode is RadioMode.Ft8)
                AlignDigiSlot(cancellationToken, periodSeconds: 15);
            else if (mode is RadioMode.Js8)
                AlignDigiSlot(cancellationToken, periodSeconds: 30);
            else if (mode is RadioMode.Wspr)
                AlignDigiSlot(cancellationToken, periodSeconds: 120);
        }, cancellationToken);

        // Read the next block while this one is turned into audio. Samples that arrive
        // while nobody is reading are dropped, and each drop is a click.
        byte[] filling = bufA;
        byte[] spare = bufB;
        Iq.DirectSamplingState? liveDs = device.DirectSampling ? new Iq.DirectSamplingState() : null;
        Task<int>? pending = Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return device.Read(filling);
        }, cancellationToken);
        try
        {
        while (!cancellationToken.IsCancellationRequested)
        {
            int n = await pending;
            byte[] filled = filling;
            filling = spare;
            spare = filled;
            byte[] toFill = filling;
            pending = Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return device.Read(toFill);
            }, cancellationToken);
            if (n <= 0)
                throw new RadioException("The radio returned no samples.");

            int usable = n / stride * stride;
            int samples = usable / stride;
            if (_liveIq.Length < samples)
                _liveIq = new Complex[samples];
            if (device.Encoding == SampleEncoding.Cu8 && !device.DirectSampling)
                Iq.Write(filled.AsSpan(0, usable), _liveIq.AsSpan(0, samples));
            else if (device.Encoding == SampleEncoding.Cu8 && liveDs is not null)
                Iq.Write(filled.AsSpan(0, usable), _liveIq.AsSpan(0, samples), directSampling: true, liveDs);
            else
                ToIq(device, filled.AsSpan(0, usable), liveDs).Samples.AsSpan(0, samples).CopyTo(_liveIq);
            ReadOnlySpan<Complex> iq = _liveIq.AsSpan(0, samples);

            if (!dmrChecked)
            {
                for (int i = 0; i < iq.Length; i++)
                    dmrProbe.Add(iq[i]);
                if (dmrProbe.Count >= dmrNeed)
                {
                    dmrChecked = true;
                    AppendNote(DmrSync.Present(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(dmrProbe), device.SampleRateHz, dmrOffsetHz)
                        ? "DMR framing found."
                        : "No DMR framing. Playing narrow FM.");
                    dmrProbe.Clear();
                }
            }

            bool framed = false;
            float[]? m17Audio = null;
            if (mode == RadioMode.Dmr)
            {
                foreach (string line in dmrReader.Push(iq, device.SampleRateHz, dmrOffsetHz, dmr))
                {
                    framed = true;
                    AppendNote(line);
                }
            }
            else if (mode == RadioMode.M17 && m17Reader is not null)
            {
                foreach (M17Update update in m17Reader.Push(iq, device.SampleRateHz, dmrOffsetHz))
                {
                    if (!string.IsNullOrWhiteSpace(update.Note))
                    {
                        framed = true;
                        AppendNote(update.Note);
                    }

                    if (update.Pcm8k is { Length: > 0 })
                    {
                        framed = true;
                        m17Audio = UpsampleAudio(update.Pcm8k, M17Reader.CodecRate, rate);
                    }
                }
            }

            bool audible = true;
            if (squelch)
            {
                int squelchLevel = Volatile.Read(ref _campSquelch);
                (bool channelHeard, double snrDb) = ChannelActivity.Measure(
                    iq,
                    device.SampleRateHz,
                    dmrOffsetHz,
                    ChannelActivity.WidthHz(mode),
                    squelchLevel);
                bool heard = framed || channelHeard;
                bool was = activity.Active;
                activity.Observe(heard, samples, (int)device.SampleRateHz);
                audible = activity.Active;
                lock (_statusGate)
                {
                    _campSnrDb = snrDb;
                    if (activity.Active != was)
                    {
                        _campActivity = activity.Active;
                        if (_campTune is not null)
                            _campTune = _campTune with { Activity = activity.Active };
                    }
                }

                if (activity.Active != was)
                {
                    SetStatus(
                        "listening",
                        1,
                        activity.Active
                            ? $"Activity at {stationHz / 1e6:0.000000} MHz ({mode})."
                            : $"Camped on {stationHz / 1e6:0.000000} MHz ({mode}), waiting for activity.");
                }
            }

            float[] fresh;
            if (mode == RadioMode.M17)
            {
                // Play Codec2 speech when a frame lands; stay quiet between frames.
                fresh = m17Audio ?? [];
                if (fresh.Length == 0)
                    continue;
                audible = true;
            }
            else
            {
                demod.Push(iq);
                fresh = demod.TakeNewAudio();
                if (fresh.Length == 0)
                    continue;
            }

            await WritePcmAsync(output, fanout, fresh, level, audible, cancellationToken);
            level = NextLevel(level, fresh);

            // Enqueue decode work only — never run Olivia/packet/VarAC on this pump thread.
            packetWorker?.TryEnqueue(fresh);
            if (ribbitWorker is not null && mode is RadioMode.Ribbit or RadioMode.Nbfm or RadioMode.Packet)
                ribbitWorker.TryEnqueue(fresh);
            varacWorker?.TryEnqueue(fresh);

            packetWorker?.Drain((kind, text) =>
            {
                if (kind == "packet")
                    AppendPacket(text);
            });
            ribbitWorker?.Drain((kind, text) =>
            {
                if (mode is RadioMode.Nbfm or RadioMode.Packet)
                    AppendNote(text + " — switch mode to Ribbit to keep decoding.");
                else
                    AppendRibbit(text);
            });
            varacWorker?.Drain((_, text) => AppendDigiLines(RadioMode.Varac, text, ref digiHeader));

            if (mode == RadioMode.Wbfm)
            {
                string[] rdsLines = demod.TakeRds().ToArray();
                if (rdsLines.Length > 0 && chain is null)
                {
                    foreach (string line in rdsLines)
                        AppendRds(line);
                }

                if (!stereoNoted && demod.WbfmStereo)
                {
                    stereoNoted = true;
                    AppendNote("Stereo pilot locked.");
                }

                if (demod.WbfmSca67 != sca67Noted)
                {
                    sca67Noted = demod.WbfmSca67;
                    if (sca67Noted)
                        AppendNote("SCA 67 kHz present — choose it under Secondary channel.");
                }

                if (demod.WbfmSca92 != sca92Noted)
                {
                    sca92Noted = demod.WbfmSca92;
                    if (sca92Noted)
                        AppendNote("SCA 92 kHz present — choose it under Secondary channel.");
                }

                if (chain is not null && (rdsLines.Length > 0 || DateTime.UtcNow >= nextText))
                {
                    float[] mono = demod.Channels > 1 ? DownmixAudio(fresh, demod.Channels) : fresh;
                    AppendLimited(recent, mono, remember);
                    if (recent.Count >= rate && DateTime.UtcNow >= nextText)
                    {
                        var frame = SignalFrame.FromAudio(recent.ToArray(), rate, 1);
                        if (rdsLines.Length > 0)
                            frame.Meta["rdsLines"] = rdsLines;
                        chain.TrySubmit(frame);
                        nextText = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                    }
                }
            }

            if (chain is not null)
                PublishPipelineTexts(chain, mode, ref digiHeader);

            if (mode == RadioMode.Cw && chain is not null)
            {
                AppendLimited(recent, fresh, remember);
                if (DateTime.UtcNow >= nextText)
                {
                    chain.TrySubmit(SignalFrame.FromAudio(recent.ToArray(), rate));
                    nextText = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                }
            }
            else if (voice && mode != RadioMode.Wbfm && chain is not null)
            {
                float[] mono = demod.Channels > 1 ? DownmixAudio(fresh, demod.Channels) : fresh;
                AppendLimited(recent, mono, sideband ? 8 * rate : remember);
                if (recent.Count >= rate && DateTime.UtcNow >= nextMorse)
                {
                    chain.TrySubmit(SignalFrame.FromAudio(recent.ToArray(), rate));
                    nextMorse = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                }
            }

            if (sideOlivia is not null)
            {
                sideOliviaPeriod.AddRange(fresh);
                sideOliviaSince += fresh.Length;
                if (sideOliviaPeriod.Count > sideOliviaKeep)
                    sideOliviaPeriod.RemoveRange(0, sideOliviaPeriod.Count - sideOliviaKeep);
                if (sideOliviaPeriod.Count >= Math.Min(sideOliviaKeep, 6 * rate) && sideOliviaSince >= sideOliviaStep)
                {
                    sideOliviaSince = 0;
                    sideOlivia.TrySubmit(SignalFrame.FromAudio(sideOliviaPeriod.ToArray(), rate));
                }

                PublishPipelineTexts(sideOlivia, RadioMode.Olivia, ref digiHeader);
            }

            if (digi && chain is not null && mode != RadioMode.Varac)
            {
                digiPeriod.AddRange(fresh);
                digiSinceSubmit += fresh.Length;
                if (slidingDigi)
                {
                    if (digiPeriod.Count > digiKeep)
                        digiPeriod.RemoveRange(0, digiPeriod.Count - digiKeep);
                    if (digiPeriod.Count >= Math.Min(digiKeep, 6 * rate) && digiSinceSubmit >= digiStep)
                    {
                        digiSinceSubmit = 0;
                        // Off-thread SignalChain worker runs Olivia/JTTY; DropOldest if still busy.
                        chain.TrySubmit(SignalFrame.FromAudio(digiPeriod.ToArray(), rate));
                    }
                }
                else if (digiPeriod.Count >= digiStep)
                {
                    while (digiPeriod.Count >= 2 * digiStep)
                        digiPeriod.RemoveRange(0, digiStep);
                    float[] block = digiPeriod.GetRange(0, digiStep).ToArray();
                    digiPeriod.RemoveRange(0, digiStep);
                    chain.TrySubmit(SignalFrame.FromAudio(block, rate));
                }
            }
        }
        }
        finally
        {
            if (chain is not null)
                PublishPipelineTexts(chain, mode, ref digiHeader);
            if (sideOlivia is not null)
                PublishPipelineTexts(sideOlivia, RadioMode.Olivia, ref digiHeader);
            packetWorker?.Drain((kind, text) => { if (kind == "packet") AppendPacket(text); });
            ribbitWorker?.Drain((_, text) => AppendRibbit(text));
            varacWorker?.Drain((_, text) => AppendDigiLines(RadioMode.Varac, text, ref digiHeader));
            packetWorker?.Dispose();
            ribbitWorker?.Dispose();
            varacWorker?.Dispose();
            xaraState?.Dispose();

            if (pending is { IsCompleted: false })
            {
                try
                {
                    await pending;
                }
                catch (Exception)
                {
                    // The read was already in flight when listening stopped.
                }
            }
        }
    }

    private static async Task WritePcmAsync(Stream? output, CampFanout? fanout, float[] samples, float level, bool audible, CancellationToken cancellationToken)
    {
        float peak = 0;
        foreach (float sample in samples)
            peak = Math.Max(peak, Math.Abs(sample));
        float gain = 0.7f / Math.Max(level, Math.Max(peak, 1e-4f));
        var bytes = new byte[samples.Length * 2];
        if (audible)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                short value = (short)Math.Clamp(samples[i] * gain * 32767f, -32767, 32767);
                bytes[i * 2] = (byte)value;
                bytes[i * 2 + 1] = (byte)(value >> 8);
            }
        }

        fanout?.Publish(bytes);
        if (output is null || !audible)
            return;
        await output.WriteAsync(bytes, cancellationToken);
        await output.FlushAsync(cancellationToken);
    }

    private static float[] DownmixAudio(float[] samples, int channels)
    {
        if (channels <= 1 || samples.Length < 2)
            return samples;
        int frames = samples.Length / channels;
        var mono = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++)
                sum += samples[i * channels + c];
            mono[i] = sum / channels;
        }

        return mono;
    }

    private static float NextLevel(float level, float[] samples)
    {
        float peak = 0;
        foreach (float sample in samples)
            peak = Math.Max(peak, Math.Abs(sample));
        return Math.Max(peak, level * 0.98f);
    }

    /// <summary>Stretch Codec2's 8 kHz speech up to the live player rate with linear steps.</summary>
    private static float[] UpsampleAudio(float[] input, int fromRate, int toRate)
    {
        if (input.Length == 0 || fromRate <= 0 || toRate <= 0 || fromRate == toRate)
            return input;
        int outLen = (int)((long)input.Length * toRate / fromRate);
        if (outLen <= 0)
            return [];
        var output = new float[outLen];
        for (int i = 0; i < outLen; i++)
        {
            double at = i * (double)fromRate / toRate;
            int i0 = (int)at;
            int i1 = Math.Min(i0 + 1, input.Length - 1);
            float frac = (float)(at - i0);
            output[i] = input[i0] * (1 - frac) + input[i1] * frac;
        }

        return output;
    }

    private static void AppendLimited(List<float> recent, float[] fresh, int max)
    {
        recent.AddRange(fresh);
        if (recent.Count > max)
            recent.RemoveRange(0, recent.Count - max);
    }

    private static bool HearsDmr(Measurement measured) =>
        DmrSync.Present(measured.Samples, measured.SampleRate, measured.Peak.FrequencyHz - measured.CenterHz);

    private static string LiveMessage(RadioMode mode, double tuneHz) => mode switch
    {
        RadioMode.Js8 =>
            $"Listening for JS8 on {tuneHz / 1e6:0.000000} MHz — waiting on 30 s UTC slots (leave it running a minute or two).",
        RadioMode.Ft8 =>
            $"Listening for FT8 on {tuneHz / 1e6:0.000000} MHz — waiting on 15 s UTC slots.",
        RadioMode.Wspr =>
            $"Listening for WSPR on {tuneHz / 1e6:0.000000} MHz — waiting on 120 s UTC slots.",
        RadioMode.Olivia or RadioMode.Jtty or RadioMode.Varac =>
            $"Listening for {mode} on {tuneHz / 1e6:0.000000} MHz.",
        _ => $"Playing {tuneHz / 1e6:0.000000} MHz ({mode}) as it arrives."
    };

    /// <summary>
    /// The tuner sits beside the station, then the station is shifted back to the middle of the voice channel.
    /// The dongle's spike is a steady tone at the tuned frequency. Sitting a station on it turns the voice into noise.
    /// Broadcast FM needs a wider step because its channel is wider than the voice offset.
    /// </summary>
    private static (double LoHz, double ShiftHz) PlaceTuner(IRadioDevice device, RadioMode mode, double stationHz)
    {
        double offset = Demodulator.TunerOffsetHz(mode);
        double above = stationHz + offset;
        if (above <= device.Capabilities.MaxFrequencyHz)
            return (above, -offset);
        return (Math.Max(device.Capabilities.MinFrequencyHz, stationHz - offset), offset);
    }

    private const double WideFmListenGainDb = 12;

    private static bool EasedGain(RadioMode mode) =>
        mode is RadioMode.Wbfm or RadioMode.Nbfm or RadioMode.Dmr or RadioMode.M17
            or RadioMode.Packet or RadioMode.Ribbit or RadioMode.Raw;

    private static void EaseWideFmGain(IRadioDevice device, RadioMode mode)
    {
        if (!EasedGain(mode))
            return;
        if (device.Capabilities.GainDb is not double gain || gain <= WideFmListenGainDb)
            return;
        device.SetGain(WideFmListenGainDb);
    }

    private static void RestoreGain(IRadioDevice device) =>
        device.SetGain(device.Capabilities.GainDb);

    /// <summary>
    /// Narrows the R820T filter to the station being demodulated. The sample rate stays 2.4 MS/s,
    /// so a scan can still see a wide hop after <see cref="ReleaseTuner"/> opens the filter again.
    /// Wide FM needs room for the 250 kHz offset. Other modes only need the voice channel.
    /// Direct sampling bypasses the tuner, so the filter is left alone below 24 MHz.
    /// </summary>
    private static void FocusTuner(IRadioDevice device, RadioMode mode)
    {
        if (device is not RtlSdrDevice rtl || device.DirectSampling)
            return;
        int bandwidth = Demodulator.TunerBandwidthHz(mode);
        try
        {
            rtl.SetBandwidth(bandwidth);
        }
        catch (RadioException)
        {
            // An older driver may not honor the filter. The digital channel filter still applies.
        }
    }

    private static void ReleaseTuner(IRadioDevice device)
    {
        if (device is not RtlSdrDevice rtl || device.DirectSampling)
            return;
        try
        {
            rtl.SetBandwidth(0);
        }
        catch (RadioException)
        {
        }
    }

    private void BeginListen(double frequencyHz)
    {
        lock (_statusGate)
        {
            if (_transcriptHz is null || Math.Abs(_transcriptHz.Value - frequencyHz) > 100)
            {
                _transcript.Clear();
                _liveText = null;
            }

            _morseWindow = "";
            _lastLiveDigiBody = "";
            _transcriptHz = frequencyHz;
        }
    }

}
