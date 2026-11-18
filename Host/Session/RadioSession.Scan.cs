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
    public Task<ScanReport> ScanAsync(string bandId, CancellationToken cancellationToken)
    {
        HamBand band = Region2BandPlan.Find(bandId)
            ?? throw new ArgumentException($"Unknown band '{bandId}'. Use list_ham_bands for ids such as 2m, 70cm, 40m, fm-bc, am-bc, noaa, marine, cb, gmrs, or dmr.");

        return BorrowRadioAsync(ct => ScanWhileHoldingAsync(band, ct), cancellationToken);
    }

    private async Task<ScanReport> ScanWhileHoldingAsync(HamBand band, CancellationToken cancellationToken)
    {
        try
        {
            using CancellationTokenSource linked = Begin(cancellationToken);
            CancellationToken ct = linked.Token;
            IRadioDevice device = RequireDevice();
            var centers = new List<double>();
            foreach (BandSegment segment in band.Segments)
            {
                if (segment.PreferredMode == RadioMode.Ft8)
                    continue;
                double start = Math.Max(segment.StartHz, device.Capabilities.MinFrequencyHz);
                double end = Math.Min(segment.EndHz, device.Capabilities.MaxFrequencyHz);
                if (end - start < 5_000)
                    continue;
                foreach (double center in HopPlanner.Centers(start, end, device.SampleRateHz, RadioLimits.HopStep(device.SampleRateHz)))
                {
                    if (centers.All(existing => Math.Abs(existing - center) > device.SampleRateHz * 0.25))
                        centers.Add(center);
                }
            }

            int dwellMs = HopPlanner.DwellMilliseconds(centers.Count);
            var activity = new List<SpeechActivity.Hit>();
            bool direct = false;
            for (int i = 0; i < centers.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                (List<double[]> frames, bool hopDirect, double centerHz, double sampleRate) = await CaptureSpectraAsync(
                    device,
                    centers[i],
                    dwellMs,
                    (slice, slices) => SetStatus(
                        "scanning",
                        (i + slice / (double)Math.Max(1, slices)) / centers.Count,
                        $"{band.Name} at {centers[i] / 1e6:0.000} MHz, listening for speech and Morse ({slice * HopPlanner.SliceMilliseconds / 1000.0:0}s)"),
                    ct);
                direct |= hopDirect;
                double half = sampleRate * 0.45;
                foreach (BandSegment segment in band.Segments)
                {
                    double start = Math.Max(segment.StartHz, centerHz - half);
                    double end = Math.Min(segment.EndHz, centerHz + half);
                    if (end - start < 5_000 || !Region2BandPlan.Covers(band, (start + end) / 2))
                        continue;
                    foreach (RadioMode mode in ModeBandwidth.ScanModes(segment.PreferredMode))
                    {
                        foreach (SpeechActivity.Hit hit in SpeechActivity.Find(
                            frames,
                            centerHz,
                            sampleRate,
                            start,
                            end,
                            mode,
                            HopPlanner.SliceMilliseconds / 1000.0))
                        {
                            if (!Region2BandPlan.Covers(band, hit.Peak.FrequencyHz))
                                continue;
                            SpectralPeak peak = hopDirect ? hit.Peak with { PossibleImage = true } : hit.Peak;
                            activity.Add(new SpeechActivity.Hit(peak, hit.Intermittent, hit.Mode));
                        }
                    }
                }
            }

            List<SpeechActivity.Hit> ranked = SpeechActivity.Distinct(activity).Take(12).ToList();
            var hits = new List<ScanHit>();
            int ignored = 0;
            for (int i = 0; i < ranked.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                SpectralPeak refined = ranked[i].Peak;
                if (SignalContent.IsClockHarmonic(refined.FrequencyHz))
                {
                    ignored++;
                    continue;
                }

                BandHint hint = Region2BandPlan.HintAt(refined.FrequencyHz);
                bool knownChannel = !string.IsNullOrWhiteSpace(hint.CallingName);
                bool framed = false;
                if (!ranked[i].Intermittent || band.Id == "dmr")
                {
                    SetStatus("scanning", 0.85 + 0.15 * i / Math.Max(1, ranked.Count), $"Measuring {refined.FrequencyHz / 1e6:0.000} MHz");
                    Measurement? measured = await MeasureAsync(device, refined.FrequencyHz, ct, knownChannel ? 6 : 8);
                    if (measured is null)
                    {
                        if (!ranked[i].Intermittent)
                            continue;
                    }
                    else
                    {
                        bool digiDial = hint.PreferredMode is RadioMode.Ft8 or RadioMode.Js8 or RadioMode.Olivia or RadioMode.Jtty or RadioMode.Wspr or RadioMode.Varac;
                        if (SignalContent.ShouldIgnore(refined.FrequencyHz, measured.Peak, digiDial, measured.Samples, measured.SampleRate, measured.CenterHz, knownChannel))
                        {
                            ignored++;
                            continue;
                        }

                        framed = DmrSync.Present(measured.Samples, measured.SampleRate, measured.Peak.FrequencyHz - measured.CenterHz);
                        if (!ranked[i].Intermittent)
                            refined = measured.Peak;
                    }
                }

                if (band.Id == "dmr" && !framed)
                    continue;

                Classification classification = ModeClassifier.Classify(refined, hint);
                // Digi dials only claim occupancy until a listen decodes text — do not inflate them to 70%.
                bool digiHint = hint.PreferredMode is RadioMode.Ft8 or RadioMode.Js8 or RadioMode.Olivia or RadioMode.Jtty or RadioMode.Wspr or RadioMode.Varac;
                if (knownChannel && hint.PreferredMode != RadioMode.Unknown && !digiHint)
                    classification = new Classification(hint.PreferredMode, Math.Max(classification.Confidence, 0.7), classification.BandwidthHz, refined.SnrDb);
                if (ranked[i].Mode == RadioMode.Cw && ModeBandwidth.Fits(RadioMode.Cw, refined.BandwidthHz))
                    classification = new Classification(RadioMode.Cw, Math.Max(classification.Confidence, 0.75), refined.BandwidthHz, refined.SnrDb);
                if (framed)
                    classification = new Classification(RadioMode.Dmr, 0.9, classification.BandwidthHz, refined.SnrDb);
                double carrier = ModeClassifier.CarrierFrequency(refined, classification.Mode);
                string source = framed ? "DMR" : await DescribeSourceAsync(carrier, classification.Mode, ct);
                hits.Add(new ScanHit(
                    carrier,
                    classification.Mode.ToString(),
                    Math.Round(classification.Confidence, 2),
                    Math.Round(classification.BandwidthHz, 0),
                    Math.Round(refined.SnrDb, 1),
                    source,
                    band.Id));
            }

            await ProbeDigiDialsAsync(device, band, hits, ct);

            hits.Sort((a, b) => b.SnrDb.CompareTo(a.SnrDb));
            string? warning = direct
                ? "This band used direct sampling. Images are possible, and an HF antenna is required."
                : options.ItuRegion == 2 ? null : "Only the ITU region 2 plan is loaded.";
            if (ignored > 0)
            {
                string spur = ignored == 1
                    ? "Ignored 1 local carrier with no voice or data."
                    : $"Ignored {ignored} local carriers with no voice or data.";
                warning = string.IsNullOrWhiteSpace(warning) ? spur : $"{warning} {spur}";
            }

            string found = $"Found {hits.Count} signal{(hits.Count == 1 ? "" : "s")} on {band.Name}.";
            SetStatus("idle", 1, ignored > 0 ? $"{found} Ignored local carriers with no voice or data." : found);
            return new ScanReport(band.Id, band.Name, device.SampleRateHz, direct, hits, warning);
        }
        catch (OperationCanceledException)
        {
            SetStatus("idle", 0, "Scan stopped.");
            throw;
        }
    }

    public Task<InspectReport> InspectAsync(double frequencyHz, CancellationToken cancellationToken) =>
        BorrowRadioAsync(ct => InspectWhileHoldingAsync(frequencyHz, ct), cancellationToken);

    private async Task<InspectReport> InspectWhileHoldingAsync(double frequencyHz, CancellationToken cancellationToken)
    {
        try
        {
            using CancellationTokenSource linked = Begin(cancellationToken);
            IRadioDevice device = RequireDevice();
            SetStatus("inspecting", 0.2, $"Inspecting {frequencyHz / 1e6:0.000000} MHz");
            Measurement? measured = await MeasureAsync(device, frequencyHz, linked.Token);
            if (measured is null)
            {
                SetStatus("idle", 1, "No strong signal at that frequency.");
                return new InspectReport(frequencyHz, RadioMode.Unknown.ToString(), 0, 0, 0, Region2BandPlan.SourceLabel(frequencyHz), device.DirectSampling);
            }

            SpectralPeak peak = measured.Peak;
            BandHint hint = Region2BandPlan.HintAt(peak.FrequencyHz);
            bool digiDial = hint.PreferredMode is RadioMode.Ft8 or RadioMode.Js8 or RadioMode.Olivia or RadioMode.Jtty or RadioMode.Wspr or RadioMode.Varac;
            if (SignalContent.ShouldIgnore(frequencyHz, peak, digiDial, measured.Samples, measured.SampleRate, measured.CenterHz))
            {
                SetStatus("idle", 1, "That carrier has no voice or data. It looks like a local spur.");
                return new InspectReport(frequencyHz, RadioMode.Unknown.ToString(), 0, 0, 0, Region2BandPlan.SourceLabel(frequencyHz), device.DirectSampling);
            }

            Classification classification = ModeClassifier.Classify(peak, hint);
            double carrier = ModeClassifier.CarrierFrequency(peak, classification.Mode);
            string source = await DescribeSourceAsync(carrier, classification.Mode, linked.Token);
            SetStatus("idle", 1, $"Inspected {carrier / 1e6:0.000000} MHz.");
            return new InspectReport(
                carrier,
                classification.Mode.ToString(),
                Math.Round(classification.Confidence, 2),
                Math.Round(classification.BandwidthHz, 0),
                Math.Round(peak.SnrDb, 1),
                source,
                device.DirectSampling);
        }
        catch (OperationCanceledException)
        {
            SetStatus("idle", 0, "Inspect stopped.");
            throw;
        }
    }

    public Task<ListenReport> ListenAsync(double frequencyHz, string? modeName, double seconds, CancellationToken cancellationToken, string? wbfmProgram = null)
    {
        seconds = Math.Clamp(double.IsFinite(seconds) && seconds > 0 ? seconds : 10, 1, 20);
        RadioMode requested = ModeClassifier.Parse(modeName);
        WbfmProgram program = WbfmPrograms.Parse(wbfmProgram);
        return BorrowRadioAsync(ct => ListenWhileHoldingAsync(frequencyHz, requested, seconds, program, ct), cancellationToken);
    }

    private async Task<ListenReport> ListenWhileHoldingAsync(double frequencyHz, RadioMode requested, double seconds, WbfmProgram wbfmProgram, CancellationToken cancellationToken)
    {
        try
        {
            BeginListen(frequencyHz);
            using CancellationTokenSource linked = Begin(cancellationToken);
            CancellationToken ct = linked.Token;
            IRadioDevice device = RequireDevice();
            RadioMode mode = requested;
            double confidence = 1;
            double tuneHz = frequencyHz;
            string? source = null;
            if (requested == RadioMode.Unknown)
            {
                SetStatus("listening", 0.1, "Classifying the signal.");
                (mode, tuneHz, confidence, _) = await ResolveAutoModeAsync(device, frequencyHz, ct);
            }

            if (mode is RadioMode.Ft8 or RadioMode.Js8 or RadioMode.Olivia or RadioMode.Jtty or RadioMode.Wspr or RadioMode.Varac)
            {
                if (Region2BandPlan.TryDigiDial(tuneHz, mode, out double dial) || Region2BandPlan.TryDigiDial(frequencyHz, mode, out dial))
                    tuneHz = dial;
                seconds = DigiListenSeconds(mode);
                // Leave ~1.2 s to tune and flush junk so capture can start on the UTC boundary.
                // JS8 uses a 30 s boundary (LCM of Normal/Fast/Turbo/Slow slots).
                if (mode is RadioMode.Ft8)
                    await WaitForDigiSlot(ct, periodSeconds: 15, earlySeconds: 2.0);
                else if (mode is RadioMode.Js8)
                    await WaitForDigiSlot(ct, periodSeconds: 30, earlySeconds: 2.0);
                else if (mode is RadioMode.Wspr)
                    await WaitForDigiSlot(ct, periodSeconds: 120, earlySeconds: 2.0);
            }

            SetStatus("listening", 0.25, "Checking published-channel frequency…");
            tuneHz = await RefinePublishedTuneAsync(device, tuneHz, mode, ct);

            source = Region2BandPlan.SourceLabel(tuneHz);
            if (TryLocation(out double latitude, out double longitude) && mode == RadioMode.Nbfm)
            {
                string? call = await repeaters.TryFindAsync(tuneHz, latitude, longitude, ct);
                if (!string.IsNullOrWhiteSpace(call))
                    source = $"{call} ({source})";
            }

            SetStatus("listening", 0.35, $"Listening {seconds:0.#} s at {tuneHz / 1e6:0.000000} MHz ({mode}).");
            RadioMode demodMode = mode;
            using SignalChain? clipChain = OpenPipeline(mode, null);
            (double loHz, double shiftHz) = PlaceTuner(device, demodMode, tuneHz);
            EaseWideFmGain(device, demodMode);
            DemodResult result;
            try
            {
                var demod = new Demodulator(
                    demodMode,
                    shiftHz,
                    device.SampleRateHz,
                    demodMode == RadioMode.Wbfm ? wbfmProgram : WbfmProgram.Main,
                    allowUsbSidebandFlip: device.DirectSampling,
                    dopplerMaxHz: Region2BandPlan.IssDopplerMaxHz(tuneHz));
                await CaptureDemodAsync(device, loHz, seconds, demod, demodMode, ct);
                result = demod.Finish(normalize: true);
            }
            finally
            {
                if (EasedGain(demodMode))
                    RestoreGain(device);
                ReleaseTuner(device);
                ClearPipeline();
            }
            string? decoded = mode == RadioMode.Dmr
                ? DmrSync.VoiceNote
                : string.IsNullOrWhiteSpace(result.Text) ? null : result.Text;
            bool sidebandMorse = false;
            if (mode is RadioMode.Usb or RadioMode.Lsb)
            {
                string morse = MorseDecoder.DecodeAudible(DownmixAudio(result.Audio, result.Channels), result.SampleRate);
                if (MorseDecoder.IsCopy(morse))
                {
                    decoded = morse;
                    sidebandMorse = true;
                }
            }

            if (!sidebandMorse && SpeechDecoder.IsVoice(mode) && result.Audio.Length >= result.SampleRate * result.Channels)
            {
                try
                {
                    string said = await speech.TranscribeAsync(DownmixAudio(result.Audio, result.Channels), result.SampleRate, ct);
                    if (!string.IsNullOrWhiteSpace(said))
                        decoded = string.IsNullOrWhiteSpace(decoded) ? said : decoded + "\n" + said;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    decoded = string.IsNullOrWhiteSpace(decoded)
                        ? "Speech was not read. " + ex.Message
                        : decoded + "\nSpeech was not read. " + ex.Message;
                }
            }
            var clip = clips.Add(
                result.Audio.Length == 0 ? WavFile.Write([], result.SampleRate, result.Channels) : WavFile.Write(result.Audio, result.SampleRate, result.Channels),
                decoded,
                tuneHz,
                mode.ToString());
            if (!string.IsNullOrWhiteSpace(decoded))
            {
                if (mode == RadioMode.Cw || sidebandMorse)
                    AppendMorse(decoded);
                else if (mode is RadioMode.Ft8 or RadioMode.Js8 or RadioMode.Olivia or RadioMode.Jtty or RadioMode.Wspr or RadioMode.Varac)
                    AppendDigi(mode, decoded);
                else if (mode == RadioMode.Packet)
                    AppendPacket(decoded);
                else if (mode == RadioMode.Ribbit)
                    AppendRibbit(decoded);
                else if (mode == RadioMode.Dmr)
                    AppendNote(decoded);
                else if (mode == RadioMode.Wbfm)
                    AppendWbfmDecoded(decoded);
                else
                    AppendSpeech(decoded);
            }

            SetStatus("idle", 1, ReadyMessage(mode, decoded));
            return new ListenReport(
                clip.Id,
                tuneHz,
                mode.ToString(),
                Math.Round(confidence, 2),
                seconds,
                $"/audio/{clip.Id}.wav",
                $"audio://clips/{clip.Id}",
                mode == RadioMode.Cw || sidebandMorse ? decoded : null,
                decoded,
                source);
        }
        catch (OperationCanceledException)
        {
            SetStatus("idle", 0, "Listen stopped.");
            throw;
        }
    }

    /// <summary>
    /// Tune, demodulate, and write 16-bit audio as it is produced. The response stays open until Stop or the browser disconnects.
    /// Morse and FT8 text are updated on the status the page already polls.
    /// </summary>
}
