using System.Globalization;
using System.Numerics;
using Microsoft.AspNetCore.Http.Features;
using AgenticSdr.Dsp;
using AgenticSdr.Radio;

namespace AgenticSdr.Host;

/// <summary>
/// What the page polls a few times a second. Phase is idle, scanning, or listening.
/// Progress runs from 0 to 1. Message is the sentence under the radio name. Error is set when the last operation failed.
/// DeviceIndex is the list position of the radio that is started, and null when the radio is stopped.
/// LiveText is Morse, FT8, or speech decoded so far during a live listen.
/// </summary>
public sealed record SessionStatus(
    bool DeviceOpen,
    string? DeviceName,
    string? Serial,
    bool DirectSampling,
    string Phase,
    double Progress,
    string? Message,
    string? Error,
    int? DeviceIndex,
    string? LiveText);

/// <summary>
/// One row in the band menu. Tunable is false when the open radio cannot reach it.
/// DirectSampling means an RTL-SDR will use its HF input. Recommended is true when the current antenna matches this band.
/// </summary>
public sealed record BandListItem(
    string Id,
    string Name,
    double StartHz,
    double EndHz,
    bool Tunable,
    bool DirectSampling,
    string? Note,
    bool Recommended = false,
    string? Fit = null);

/// <summary>
/// One strong signal from a scan. Frequency is in hertz. Confidence is 0 to 1.
/// Bandwidth is the width of the peak. SnrDb is how far it stands above the noise. Source is the band-plan label.
/// </summary>
public sealed record ScanHit(
    double FrequencyHz,
    string Mode,
    double Confidence,
    double BandwidthHz,
    double SnrDb,
    string Source,
    string BandId);

/// <summary>The result of scanning one band: the hits, whether direct sampling was used, and a warning when images are possible.</summary>
public sealed record ScanReport(
    string BandId,
    string BandName,
    double SampleRateHz,
    bool DirectSampling,
    IReadOnlyList<ScanHit> Hits,
    string? Warning);

/// <summary>A fresh measurement of one frequency: mode guess, width, strength, and the band-plan label.</summary>
public sealed record InspectReport(
    double FrequencyHz,
    string Mode,
    double Confidence,
    double BandwidthHz,
    double SnrDb,
    string Source,
    bool DirectSampling);

/// <summary>
/// A finished listen. AudioUrl is the WAV the page plays. Morse and Text hold decoded CW or FT8 when there is any.
/// Source is the band-plan label for the tuned frequency.
/// </summary>
public sealed record ListenReport(
    string ClipId,
    double FrequencyHz,
    string Mode,
    double Confidence,
    double Seconds,
    string AudioUrl,
    string AudioUri,
    string? Morse,
    string? Text,
    string? Source);

/// <summary>
/// The one owner of the USB radio. The web page and the AI tools both call this.
/// A lock keeps a scan from running during a listen. Stop cancels whichever is in progress.
/// </summary>
public sealed class RadioSession(
    SdrOptions options,
    ClipStore clips,
    RepeaterLookup repeaters,
    SpeechDecoder speech,
    ILogger<RadioSession> log)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _statusGate = new();
    private CancellationTokenSource _cancel = new();
    private IRadioDevice? _device;
    private string _phase = "idle";
    private double _progress;
    private string? _message = "Radio is closed.";
    private string? _error;
    private string? _deviceName;
    private string? _serial;
    private int? _deviceIndex;
    private string? _liveText;
    private bool _directSampling;
    private AntennaSpec? _antenna;
    private Complex[] _liveIq = [];

    public SessionStatus Status
    {
        get
        {
            lock (_statusGate)
            {
                return new SessionStatus(
                    _device is not null,
                    _deviceName,
                    _serial,
                    _directSampling,
                    _phase,
                    _progress,
                    _message,
                    _error,
                    _deviceIndex,
                    _liveText);
            }
        }
    }

    public void Note(string message)
    {
        lock (_statusGate)
        {
            _error = message;
            _message = message;
        }
    }

    public void Stop()
    {
        lock (_statusGate)
            _cancel.Cancel();
    }

    public static IReadOnlyList<RadioDeviceInfo> ListDevices() => RadioDevices.List();

    public IReadOnlyList<BandListItem> ListBands()
    {
        double min = RadioLimits.MinFrequencyHz;
        double max = RadioLimits.MaxFrequencyHz;
        if (_device is not null)
        {
            min = _device.Capabilities.MinFrequencyHz;
            max = _device.Capabilities.MaxFrequencyHz;
        }

        bool regionWarning = options.ItuRegion != 2;
        AntennaAdvice? advice = _antenna is null ? null : AntennaPlan.Advise(_antenna);
        var rank = advice?.Bands.Select((match, index) => (match.BandId, index)).ToDictionary(item => item.BandId, item => item.index);
        var items = Region2BandPlan.Bands.Select((band, index) =>
        {
            bool tunable = band.EndHz > min && band.StartHz < max;
            bool direct = _device?.Capabilities.DirectSamplingBelow25MHz == true
                && band.StartHz < RadioLimits.DirectSamplingCutoffHz;
            AntennaBandMatch? match = advice?.Bands.FirstOrDefault(item => item.BandId == band.Id);
            var notes = new List<string>();
            if (!tunable)
                notes.Add("Outside this radio's tuning range.");
            else if (regionWarning)
                notes.Add("Only the ITU region 2 plan is loaded.");
            else if (direct)
                notes.Add("Direct sampling. An HF antenna is required; a VHF whip will hear little here.");
            if (match is not null)
                notes.Add(match.Reason);
            return (index, Item: new BandListItem(
                band.Id,
                band.Name,
                band.StartHz,
                band.EndHz,
                tunable,
                direct,
                notes.Count == 0 ? null : string.Join(" ", notes),
                match is not null,
                match?.Reason));
        }).ToList();

        items.Sort((a, b) =>
        {
            int ar = rank is not null && rank.TryGetValue(a.Item.Id, out int ai) ? ai : int.MaxValue;
            int br = rank is not null && rank.TryGetValue(b.Item.Id, out int bi) ? bi : int.MaxValue;
            int compared = ar.CompareTo(br);
            return compared != 0 ? compared : a.index.CompareTo(b.index);
        });
        return items.Select(item => item.Item).ToList();
    }

    public AntennaAdvice Antenna() => _antenna is null ? AntennaPlan.None() : AntennaPlan.Advise(_antenna);

    public AntennaAdvice SetAntenna(double length, string? unit, string? orientation, double? azimuthDegrees)
    {
        _antenna = AntennaPlan.Parse(length, unit, orientation, azimuthDegrees);
        return AntennaPlan.Advise(_antenna);
    }

    /// <summary>
    /// Listen at a handful of spots and set the antenna from where the noise floor is strongest.
    /// The typed length is left unchanged when the floors are too even to choose.
    /// </summary>
    public async Task<AntennaAdvice> GuessAntennaAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using CancellationTokenSource linked = Begin(cancellationToken);
            CancellationToken ct = linked.Token;
            IRadioDevice device = RequireDevice();
            List<double> probes = AntennaGuess.ProbesInRange(device.Capabilities.MinFrequencyHz, device.Capabilities.MaxFrequencyHz).ToList();
            if (probes.Count < 3)
                throw new InvalidOperationException("This radio cannot tune enough of the dial to guess an antenna.");

            double? savedGain = device.Capabilities.GainDb;
            if (savedGain is > 8)
                device.SetGain(8);
            var readings = new List<SignalStrength>(probes.Count);
            try
            {
                for (int i = 0; i < probes.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    SetStatus("scanning", i / (double)probes.Count, $"Listening for the antenna near {probes[i] / 1e6:0.00} MHz");
                    Capture capture = await CaptureAsync(device, probes[i], 80, ct);
                    double[] db = SpectrumAnalyzer.AveragePowerDb(capture.Samples, 4096);
                    readings.Add(new SignalStrength(probes[i], Percentile(db, 0.15)));
                    log.LogInformation("Antenna floor {MHz:0.00} MHz {Floor:0.0} dB", probes[i] / 1e6, readings[^1].FloorDb);
                }
            }
            finally
            {
                if (savedGain is > 8)
                    device.SetGain(savedGain);
            }

            if (!AntennaGuess.TryFromFloors(readings, out AntennaSpec spec, out string note))
                throw new InvalidOperationException(note);

            _antenna = spec;
            AntennaAdvice advice = AntennaPlan.Advise(spec);
            SetStatus("idle", 1, "Antenna guessed from signal strength.");
            return advice with { Summary = note + " " + advice.Summary };
        }
        catch (OperationCanceledException)
        {
            SetStatus("idle", 0, "Antenna guess stopped.");
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static double Percentile(double[] values, double fraction)
    {
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        int index = (int)Math.Clamp(Math.Round(fraction * (sorted.Length - 1)), 0, sorted.Length - 1);
        return sorted[index];
    }

    public async Task OpenAsync(int index, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _device?.Dispose();
            _device = null;
            IRadioDevice device = await Task.Run(() =>
            {
                IReadOnlyList<RadioDeviceInfo> devices = RadioDevices.List();
                if (index < 0 || index >= devices.Count)
                    throw new RadioException($"No SDR at index {index}. {devices.Count} device(s) are visible.");
                return RadioDevices.Open(devices[index], options.SampleRateHz, options.AutoGain ? null : options.GainDb, options.Ppm);
            }, cancellationToken);
            _device = device;
            lock (_statusGate)
            {
                _deviceName = device.Capabilities.Name;
                _serial = device.Capabilities.Serial;
                _deviceIndex = index;
                _directSampling = false;
                _phase = "idle";
                _progress = 0;
                _error = null;
                _message = $"Radio started: {device.Capabilities.Name}.";
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Close()
    {
        Stop();
        _gate.Wait();
        try
        {
            _device?.Dispose();
            _device = null;
            lock (_statusGate)
            {
                _deviceName = null;
                _serial = null;
                _deviceIndex = null;
                _liveText = null;
                _directSampling = false;
                _phase = "idle";
                _progress = 0;
                _message = "Radio is stopped.";
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public RadioCapabilities Capabilities()
    {
        IRadioDevice device = _device ?? throw new InvalidOperationException("Open the radio before reading capabilities.");
        return device.Capabilities;
    }

    public async Task<ScanReport> ScanAsync(string bandId, CancellationToken cancellationToken)
    {
        HamBand band = Region2BandPlan.Find(bandId)
            ?? throw new ArgumentException($"Unknown band '{bandId}'. Use list_ham_bands for ids such as 2m, 70cm, 40m, fm-bc, am-bc, noaa, cb, gmrs, or dmr.");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            using CancellationTokenSource linked = Begin(cancellationToken);
            CancellationToken ct = linked.Token;
            IRadioDevice device = RequireDevice();
            var centers = new List<double>();
            foreach ((double startHz, double endHz) in Region2BandPlan.Coverage(band))
            {
                double start = Math.Max(startHz, device.Capabilities.MinFrequencyHz);
                double end = Math.Min(endHz, device.Capabilities.MaxFrequencyHz);
                if (end - start < 5_000)
                    continue;
                centers.AddRange(HopPlanner.Centers(start, end, device.SampleRateHz, RadioLimits.HopStep(device.SampleRateHz)));
            }
            var peaks = new List<SpectralPeak>();
            bool direct = false;
            for (int i = 0; i < centers.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                SetStatus("scanning", i / (double)centers.Count, $"{band.Name} at {centers[i] / 1e6:0.000} MHz");
                Capture capture = await CaptureAsync(device, centers[i], 120, ct);
                direct |= capture.DirectSampling;
                double[] db = SpectrumAnalyzer.AveragePowerDb(capture.Samples, 4096);
                bool wide = Region2BandPlan.ScanBandwidthHz(band) > 20_000;
                foreach (SpectralPeak peak in PeakPicker.Find(
                    db,
                    capture.CenterHz,
                    capture.SampleRate,
                    maxBandwidthHz: Region2BandPlan.ScanBandwidthHz(band),
                    collapseImages: capture.RealValued,
                    contourDropDb: wide ? 30 : 18,
                    minBandwidthHz: wide ? 40_000 : 0))
                {
                    if (!Region2BandPlan.Covers(band, peak.FrequencyHz))
                        continue;
                    peaks.Add(capture.DirectSampling ? peak with { PossibleImage = true } : peak);
                }

                foreach (CallingChannel channel in band.Calling)
                {
                    if (channel.Mode == RadioMode.Ft8)
                        continue;
                    if (Math.Abs(channel.FrequencyHz - capture.CenterHz) > capture.SampleRate * 0.45)
                        continue;
                    if (!Region2BandPlan.Covers(band, channel.FrequencyHz))
                        continue;
                    if (peaks.Exists(peak => Math.Abs(peak.FrequencyHz - channel.FrequencyHz) <= channel.ToleranceHz))
                        continue;
                    SpectralPeak? channelPeak = PeakPicker.LoudestIn(
                        db,
                        capture.CenterHz,
                        capture.SampleRate,
                        channel.FrequencyHz,
                        channel.ToleranceHz,
                        minSnrDb: 6);
                    if (channelPeak is null)
                        continue;
                    peaks.Add(capture.DirectSampling ? channelPeak with { PossibleImage = true } : channelPeak);
                }
            }

            List<SpectralPeak> ranked = PeakPicker.Merge(peaks, Region2BandPlan.MergeDistanceHz(band))
                .OrderByDescending(p => p.SnrDb)
                .Take(8)
                .ToList();
            var hits = new List<ScanHit>();
            int ignored = 0;
            for (int i = 0; i < ranked.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                SetStatus("scanning", 0.65 + 0.35 * i / Math.Max(1, ranked.Count), $"Measuring {ranked[i].FrequencyHz / 1e6:0.000} MHz");
                bool named = !string.IsNullOrWhiteSpace(Region2BandPlan.HintAt(ranked[i].FrequencyHz).CallingName);
                Measurement? measured = await MeasureAsync(device, ranked[i].FrequencyHz, ct, named ? 6 : 8);
                if (measured is null || measured.Peak.SnrDb < (named ? 6 : 8))
                    continue;
                SpectralPeak refined = measured.Peak;
                if (!Region2BandPlan.Covers(band, refined.FrequencyHz, 8_000))
                    continue;

                BandHint hint = Region2BandPlan.HintAt(refined.FrequencyHz);
                bool ft8 = hint.PreferredMode == RadioMode.Ft8;
                bool knownChannel = !string.IsNullOrWhiteSpace(hint.CallingName);
                if (SignalContent.ShouldIgnore(ranked[i].FrequencyHz, refined, ft8, measured.Samples, measured.SampleRate, measured.CenterHz, knownChannel))
                {
                    ignored++;
                    continue;
                }

                bool framed = DmrSync.Present(measured.Samples, measured.SampleRate, refined.FrequencyHz - measured.CenterHz);
                if (band.Id == "dmr" && !framed)
                    continue;

                Classification classification = ModeClassifier.Classify(refined, hint);
                if (knownChannel && hint.PreferredMode != RadioMode.Unknown)
                    classification = new Classification(hint.PreferredMode, Math.Max(classification.Confidence, 0.7), classification.BandwidthHz, refined.SnrDb);
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

            string found = $"Found {hits.Count} strong signal{(hits.Count == 1 ? "" : "s")} on {band.Name}.";
            SetStatus("idle", 1, ignored > 0 ? $"{found} Ignored local carriers with no voice or data." : found);
            return new ScanReport(band.Id, band.Name, device.SampleRateHz, direct, hits, warning);
        }
        catch (OperationCanceledException)
        {
            SetStatus("idle", 0, "Scan stopped.");
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<InspectReport> InspectAsync(double frequencyHz, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
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
            if (SignalContent.ShouldIgnore(frequencyHz, peak, hint.PreferredMode == RadioMode.Ft8, measured.Samples, measured.SampleRate, measured.CenterHz))
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
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ListenReport> ListenAsync(double frequencyHz, string? modeName, double seconds, CancellationToken cancellationToken)
    {
        seconds = Math.Clamp(double.IsFinite(seconds) && seconds > 0 ? seconds : 10, 1, 20);
        RadioMode requested = ModeClassifier.Parse(modeName);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using CancellationTokenSource linked = Begin(cancellationToken);
            CancellationToken ct = linked.Token;
            IRadioDevice device = RequireDevice();
            RadioMode mode = requested;
            double confidence = 1;
            double tuneHz = frequencyHz;
            string? source = null;
            if (requested == RadioMode.Unknown && Region2BandPlan.TryFt8Dial(frequencyHz, out double ft8Dial))
            {
                mode = RadioMode.Ft8;
                tuneHz = ft8Dial;
                confidence = 0.8;
            }
            else if (requested == RadioMode.Unknown)
            {
                SetStatus("listening", 0.1, "Classifying the signal.");
                Measurement? measured = await MeasureAsync(device, frequencyHz, ct);
                if (measured is null)
                {
                    mode = RadioMode.Nbfm;
                    confidence = 0.2;
                }
                else
                {
                    BandHint hint = Region2BandPlan.HintAt(measured.Peak.FrequencyHz);
                    Classification classification = ModeClassifier.Classify(measured.Peak, hint);
                    mode = classification.Mode == RadioMode.Unknown ? RadioMode.Nbfm : classification.Mode;
                    confidence = classification.Confidence;
                    tuneHz = ModeClassifier.CarrierFrequency(measured.Peak, mode);
                    if (HearsDmr(measured))
                    {
                        mode = RadioMode.Dmr;
                        confidence = 0.9;
                    }
                }
            }

            if (mode == RadioMode.Ft8)
            {
                if (Region2BandPlan.TryFt8Dial(tuneHz, out double dial) || Region2BandPlan.TryFt8Dial(frequencyHz, out dial))
                    tuneHz = dial;
                seconds = 15;
                await WaitForFt8Slot(ct);
            }

            source = Region2BandPlan.SourceLabel(tuneHz);
            if (TryLocation(out double latitude, out double longitude) && mode == RadioMode.Nbfm)
            {
                string? call = await repeaters.TryFindAsync(tuneHz, latitude, longitude, ct);
                if (!string.IsNullOrWhiteSpace(call))
                    source = $"{call} ({source})";
            }

            SetStatus("listening", 0.35, $"Listening {seconds:0.#} s at {tuneHz / 1e6:0.000000} MHz ({mode}).");
            RadioMode demodMode = mode == RadioMode.Dmr ? RadioMode.Nbfm : mode;
            (double loHz, double shiftHz) = PlaceTuner(device, demodMode, tuneHz);
            EaseWideFmGain(device, demodMode);
            DemodResult result;
            try
            {
                var demod = new Demodulator(demodMode, shiftHz, device.SampleRateHz);
                await CaptureDemodAsync(device, loHz, seconds, demod, demodMode, ct);
                result = demod.Finish(normalize: true);
            }
            finally
            {
                if (demodMode is RadioMode.Wbfm or RadioMode.Nbfm)
                    RestoreGain(device);
                ReleaseTuner(device);
            }
            string? decoded = mode == RadioMode.Dmr
                ? DmrSync.VoiceNote
                : string.IsNullOrWhiteSpace(result.Text) ? null : result.Text;
            if (SpeechDecoder.IsVoice(mode) && result.Audio.Length >= result.SampleRate)
            {
                try
                {
                    string said = await speech.TranscribeAsync(result.Audio, result.SampleRate, ct);
                    if (!string.IsNullOrWhiteSpace(said))
                        decoded = said;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    decoded = "Speech was not read. " + ex.Message;
                }
            }
            var clip = clips.Add(result.Audio.Length == 0 ? WavFile.Write([], result.SampleRate) : WavFile.Write(result.Audio, result.SampleRate), decoded, tuneHz, mode.ToString());
            SetStatus("idle", 1, ReadyMessage(mode, decoded));
            return new ListenReport(
                clip.Id,
                tuneHz,
                mode.ToString(),
                Math.Round(confidence, 2),
                seconds,
                $"/audio/{clip.Id}.wav",
                $"audio://clips/{clip.Id}",
                mode == RadioMode.Cw ? decoded : null,
                decoded,
                source);
        }
        catch (OperationCanceledException)
        {
            SetStatus("idle", 0, "Listen stopped.");
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Tune, demodulate, and write 16-bit audio as it is produced. The response stays open until Stop or the browser disconnects.
    /// Morse and FT8 text are updated on the status the page already polls.
    /// </summary>
    public async Task WriteLiveAsync(double frequencyHz, string? modeName, HttpResponse response, CancellationToken cancellationToken)
    {
        RadioMode requested = ModeClassifier.Parse(modeName);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            lock (_statusGate)
                _liveText = null;
            using CancellationTokenSource linked = Begin(cancellationToken);
            CancellationToken ct = linked.Token;
            IRadioDevice device = RequireDevice();
            RadioMode mode = requested;
            double tuneHz = frequencyHz;
            bool dmrFramed = false;
            if (requested == RadioMode.Unknown && Region2BandPlan.TryFt8Dial(frequencyHz, out double ft8Dial))
            {
                mode = RadioMode.Ft8;
                tuneHz = ft8Dial;
            }
            else if (requested == RadioMode.Unknown)
            {
                SetStatus("listening", 0.1, "Classifying the signal.");
                Measurement? measured = await MeasureAsync(device, frequencyHz, ct);
                if (measured is null)
                    mode = RadioMode.Nbfm;
                else
                {
                    BandHint hint = Region2BandPlan.HintAt(measured.Peak.FrequencyHz);
                    Classification classification = ModeClassifier.Classify(measured.Peak, hint);
                    mode = classification.Mode == RadioMode.Unknown ? RadioMode.Nbfm : classification.Mode;
                    tuneHz = ModeClassifier.CarrierFrequency(measured.Peak, mode);
                    if (HearsDmr(measured))
                    {
                        mode = RadioMode.Dmr;
                        dmrFramed = true;
                    }
                }
            }

            if (mode == RadioMode.Ft8 && (Region2BandPlan.TryFt8Dial(tuneHz, out double dial) || Region2BandPlan.TryFt8Dial(frequencyHz, out dial)))
            {
                tuneHz = dial;
                await WaitForFt8Slot(ct);
            }

            string? source = Region2BandPlan.SourceLabel(tuneHz);
            if (TryLocation(out double latitude, out double longitude) && mode == RadioMode.Nbfm)
            {
                string? call = await repeaters.TryFindAsync(tuneHz, latitude, longitude, ct);
                if (!string.IsNullOrWhiteSpace(call))
                    source = $"{call} ({source})";
            }

            if (mode == RadioMode.Dmr)
                source = "DMR";
            RadioMode demodMode = mode == RadioMode.Dmr ? RadioMode.Nbfm : mode;
            (double loHz, double shiftHz) = PlaceTuner(device, demodMode, tuneHz);
            EaseWideFmGain(device, demodMode);
            var demod = new Demodulator(demodMode, shiftHz, device.SampleRateHz);
            response.Headers["X-Audio-Rate"] = demod.PlaybackRate.ToString(CultureInfo.InvariantCulture);
            response.Headers["X-Tune-Hz"] = tuneHz.ToString(CultureInfo.InvariantCulture);
            response.Headers["X-Mode"] = mode.ToString();
            response.Headers["X-Source"] = source ?? "";
            response.ContentType = "application/octet-stream";
            response.Headers.CacheControl = "no-store";
            response.HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            await response.StartAsync(ct);

            SetStatus("listening", 1, LiveMessage(mode, tuneHz));
            if (mode is RadioMode.Cw or RadioMode.Ft8)
                SetLive(mode == RadioMode.Cw ? "Listening for Morse…" : "Listening for FT8…");
            else if (dmrFramed)
                SetLive(DmrSync.VoiceNote);
            else if (SpeechDecoder.IsVoice(mode))
                SetLive("Listening for speech…");

            try
            {
                await PumpLiveAsync(device, loHz, mode, demod, response.Body, mode == RadioMode.Dmr && !dmrFramed, ct);
            }
            finally
            {
                if (demodMode is RadioMode.Wbfm or RadioMode.Nbfm)
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

            if (!response.HasStarted)
                throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task PumpLiveAsync(
        IRadioDevice device,
        double frequencyHz,
        RadioMode mode,
        Demodulator demod,
        Stream output,
        bool recheckDmr,
        CancellationToken cancellationToken)
    {
        int stride = device.Encoding == SampleEncoding.Cu8 ? 2 : 8;
        int blockBytes = Aligned(device, 262_144);
        var bufA = new byte[blockBytes];
        var bufB = new byte[blockBytes];
        var recent = new List<float>();
        var dmrProbe = new List<Complex>();
        bool dmrChecked = !recheckDmr;
        int dmrNeed = (int)(device.SampleRateHz * 0.12);
        var ft8Period = new List<float>();
        string ft8Log = "";
        Task<string>? speechTask = null;
        bool voice = SpeechDecoder.IsVoice(mode);
        int rate = demod.PlaybackRate;
        int remember = voice ? 6 * rate : 8 * rate;
        int ft8Slot = 15 * rate;
        var nextText = DateTime.UtcNow + TimeSpan.FromSeconds(1);
        float level = 0.05f;

        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            device.Tune(frequencyHz);
            lock (_statusGate)
                _directSampling = device.DirectSampling;
            FocusTuner(device, mode);
            var junk = new byte[Aligned(device, 32 * 1024)];
            device.Read(junk);
        }, cancellationToken);

        // Read the next block while this one is turned into audio. Samples that arrive
        // while nobody is reading are dropped, and each drop is a click.
        byte[] filling = bufA;
        byte[] spare = bufB;
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
            else
                ToIq(device, filled.AsSpan(0, usable)).Samples.AsSpan(0, samples).CopyTo(_liveIq);
            ReadOnlySpan<Complex> iq = _liveIq.AsSpan(0, samples);

            if (!dmrChecked)
            {
                for (int i = 0; i < iq.Length; i++)
                    dmrProbe.Add(iq[i]);
                if (dmrProbe.Count >= dmrNeed)
                {
                    dmrChecked = true;
                    SetLive(DmrSync.Present(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(dmrProbe), device.SampleRateHz, 0)
                        ? DmrSync.VoiceNote
                        : "No DMR framing. Playing narrow FM.");
                    dmrProbe.Clear();
                }
            }

            demod.Push(iq);
            float[] fresh = demod.TakeNewAudio();
            if (fresh.Length == 0)
                continue;

            await WritePcmAsync(output, fresh, level, cancellationToken);
            level = NextLevel(level, fresh);

            if (mode == RadioMode.Cw)
            {
                AppendLimited(recent, fresh, remember);
                if (DateTime.UtcNow >= nextText)
                {
                    string text = MorseDecoder.Decode(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(recent), rate);
                    SetLive(string.IsNullOrWhiteSpace(text) ? "Listening for Morse…" : text);
                    nextText = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                }
            }
            else if (voice)
            {
                AppendLimited(recent, fresh, remember);
                if (speechTask is { IsCompleted: true })
                {
                    try
                    {
                        string said = await speechTask;
                        if (!string.IsNullOrWhiteSpace(said))
                            SetLive(said);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        SetLive("Speech was not read. " + ex.Message);
                    }

                    speechTask = null;
                }

                if (speechTask is null && recent.Count >= remember && DateTime.UtcNow >= nextText)
                {
                    float[] clip = recent.ToArray();
                    speechTask = speech.TranscribeAsync(clip, rate, cancellationToken);
                    nextText = DateTime.UtcNow + TimeSpan.FromSeconds(6);
                }
            }
            else if (mode == RadioMode.Ft8)
            {
                ft8Period.AddRange(fresh);
                while (ft8Period.Count >= ft8Slot)
                {
                    var block = ft8Period.GetRange(0, ft8Slot);
                    ft8Period.RemoveRange(0, ft8Slot);
                    string messages = Ft8Decoder.Format(Ft8Decoder.Decode(block.ToArray(), rate));
                    if (!string.IsNullOrWhiteSpace(messages))
                        ft8Log = string.IsNullOrEmpty(ft8Log) ? messages : ft8Log + "\n" + messages;
                    SetLive(string.IsNullOrWhiteSpace(ft8Log) ? "Listening for FT8…" : ft8Log);
                }
            }
        }
        }
        finally
        {
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

    private static async Task WritePcmAsync(Stream output, float[] samples, float level, CancellationToken cancellationToken)
    {
        float peak = 0;
        foreach (float sample in samples)
            peak = Math.Max(peak, Math.Abs(sample));
        float gain = 0.7f / Math.Max(level, Math.Max(peak, 1e-4f));
        var bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            short value = (short)Math.Clamp(samples[i] * gain * 32767f, -32767, 32767);
            bytes[i * 2] = (byte)value;
            bytes[i * 2 + 1] = (byte)(value >> 8);
        }

        await output.WriteAsync(bytes, cancellationToken);
        await output.FlushAsync(cancellationToken);
    }

    private static float NextLevel(float level, float[] samples)
    {
        float peak = 0;
        foreach (float sample in samples)
            peak = Math.Max(peak, Math.Abs(sample));
        return Math.Max(peak, level * 0.98f);
    }

    private static void AppendLimited(List<float> recent, float[] fresh, int max)
    {
        recent.AddRange(fresh);
        if (recent.Count > max)
            recent.RemoveRange(0, recent.Count - max);
    }

    private static bool HearsDmr(Measurement measured) =>
        DmrSync.Present(measured.Samples, measured.SampleRate, measured.Peak.FrequencyHz - measured.CenterHz);

    private static string LiveMessage(RadioMode mode, double tuneHz) =>
        $"Playing {tuneHz / 1e6:0.000000} MHz ({mode}) as it arrives.";

    /// <summary>
    /// The tuner sits beside the station, then the station is shifted back to the middle of the voice channel.
    /// The dongle's spike is a steady tone at the tuned frequency. Sitting a station on it turns the voice into noise.
    /// Broadcast FM needs a wider step because its channel is wider than the voice offset.
    /// </summary>
    private static (double LoHz, double ShiftHz) PlaceTuner(IRadioDevice device, RadioMode mode, double stationHz)
    {
        double offset = mode == RadioMode.Wbfm ? Demodulator.WideFmOffsetHz : Demodulator.VoiceOffsetHz;
        double above = stationHz + offset;
        if (above <= device.Capabilities.MaxFrequencyHz)
            return (above, -offset);
        return (Math.Max(device.Capabilities.MinFrequencyHz, stationHz - offset), offset);
    }

    private const double WideFmListenGainDb = 12;

    private static void EaseWideFmGain(IRadioDevice device, RadioMode mode)
    {
        if (mode is not (RadioMode.Wbfm or RadioMode.Nbfm))
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
        int bandwidth = mode == RadioMode.Wbfm ? 900_000 : 300_000;
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

    private void SetLive(string text)
    {
        lock (_statusGate)
            _liveText = text;
    }

    private async Task<Measurement?> MeasureAsync(IRadioDevice device, double frequencyHz, CancellationToken cancellationToken, double thresholdDb = 8)
    {
        double offset = Math.Min(50_000, device.SampleRateHz * 0.2);
        double tune = Math.Clamp(frequencyHz + offset, device.Capabilities.MinFrequencyHz, device.Capabilities.MaxFrequencyHz);
        Capture capture = await CaptureAsync(device, tune, 160, cancellationToken);
        double[] db = SpectrumAnalyzer.AveragePowerDb(capture.Samples, 8192);
        bool wide = Region2BandPlan.HintAt(frequencyHz).PreferredMode == RadioMode.Wbfm;
        List<SpectralPeak> peaks = PeakPicker.Find(
            db,
            capture.CenterHz,
            capture.SampleRate,
            thresholdDb: thresholdDb,
            maxBandwidthHz: wide ? 300_000 : 20_000,
            collapseImages: capture.RealValued,
            contourDropDb: wide ? 30 : 18,
            minBandwidthHz: wide ? 40_000 : 0);
        SpectralPeak? best = null;
        double bestDistance = double.MaxValue;
        foreach (SpectralPeak peak in peaks)
        {
            double distance = Math.Abs(peak.FrequencyHz - frequencyHz);
            if (distance < bestDistance)
            {
                best = peak;
                bestDistance = distance;
            }
        }

        if (best is null || bestDistance > 150_000)
            return null;
        SpectralPeak chosen = capture.DirectSampling ? best with { PossibleImage = true } : best;
        return new Measurement(chosen, capture.Samples, capture.CenterHz, capture.SampleRate);
    }

    private async Task<Capture> CaptureAsync(IRadioDevice device, double frequencyHz, int milliseconds, CancellationToken cancellationToken)
    {
        int stride = device.Encoding == SampleEncoding.Cu8 ? 2 : 8;
        int unit = Math.Max(1, device.ReadAlignment / stride);
        int samples = Math.Max(unit, (int)(device.SampleRateHz * milliseconds / 1000.0));
        samples = Math.Max(unit, samples / unit * unit);
        var buffer = new byte[samples * stride];
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            device.Tune(frequencyHz);
            lock (_statusGate)
                _directSampling = device.DirectSampling;
            var junk = new byte[Aligned(device, 32 * 1024)];
            device.Read(junk);
            int got = 0;
            var chunk = new byte[Aligned(device, 262_144)];
            while (got + device.ReadAlignment <= buffer.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int ask = Math.Min(chunk.Length, (buffer.Length - got) / device.ReadAlignment * device.ReadAlignment);
                byte[] block = ask == chunk.Length ? chunk : new byte[ask];
                int n = device.Read(block);
                if (n <= 0)
                    throw new RadioException("The radio returned no samples.");
                int copy = Math.Min(n, buffer.Length - got);
                Buffer.BlockCopy(block, 0, buffer, got, copy);
                got += copy;
            }
        }, cancellationToken);

        (Complex[] iq, bool realValued) = ToIq(device, buffer);
        return new Capture(iq, device.CenterFrequencyHz, device.SampleRateHz, device.DirectSampling, realValued);
    }

    private async Task CaptureDemodAsync(
        IRadioDevice device,
        double frequencyHz,
        double seconds,
        Demodulator demod,
        RadioMode mode,
        CancellationToken cancellationToken)
    {
        long target = (long)(seconds * device.SampleRateHz);
        int stride = device.Encoding == SampleEncoding.Cu8 ? 2 : 8;
        var chunk = new byte[Aligned(device, 262_144)];
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            device.Tune(frequencyHz);
            lock (_statusGate)
                _directSampling = device.DirectSampling;
            FocusTuner(device, mode);
            var junk = new byte[Aligned(device, 32 * 1024)];
            device.Read(junk);
            long got = 0;
            while (got < target)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int n = device.Read(chunk);
                if (n <= 0)
                    throw new RadioException("The radio returned no samples.");
                int usable = n / stride * stride;
                (Complex[] iq, _) = ToIq(device, chunk.AsSpan(0, usable));
                demod.Push(iq);
                got += iq.Length;
                SetStatus("listening", 0.35 + 0.6 * Math.Min(1, got / (double)target), $"Receiving {frequencyHz / 1e6:0.000000} MHz");
            }
        }, cancellationToken);
    }

    private static int Aligned(IRadioDevice device, int bytes)
    {
        int align = device.ReadAlignment;
        if (bytes < align)
            bytes = align;
        return bytes / align * align;
    }

    private static (Complex[] Samples, bool RealValued) ToIq(IRadioDevice device, ReadOnlySpan<byte> bytes)
    {
        if (device.Encoding == SampleEncoding.Cu8)
            return Iq.Convert(bytes, device.DirectSampling);

        int count = bytes.Length / 8;
        var iq = new Complex[count];
        for (int i = 0; i < count; i++)
        {
            float real = BitConverter.ToSingle(bytes.Slice(i * 8, 4));
            float imag = BitConverter.ToSingle(bytes.Slice(i * 8 + 4, 4));
            iq[i] = new Complex(real, imag);
        }

        return (iq, false);
    }

    private async Task WaitForFt8Slot(CancellationToken cancellationToken)
    {
        double into = DateTime.UtcNow.TimeOfDay.TotalSeconds % 15.0;
        if (into < 0.2)
            return;

        double wait = 15.0 - into;
        var started = DateTime.UtcNow;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double left = wait - (DateTime.UtcNow - started).TotalSeconds;
            if (left <= 0.05)
                break;
            SetStatus("listening", 0.05 + 0.25 * (1 - Math.Clamp(left / wait, 0, 1)), $"Waiting {Math.Ceiling(left):0} s for the next 15 s FT8 slot.");
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(250, Math.Max(20, left * 1000))), cancellationToken);
        }
    }

    private static string ReadyMessage(RadioMode mode, string? decoded) => mode switch
    {
        RadioMode.Cw when string.IsNullOrWhiteSpace(decoded) => "CW clip ready.",
        RadioMode.Cw => $"CW clip ready: {decoded}",
        RadioMode.Ft8 when string.IsNullOrWhiteSpace(decoded) => "FT8 clip ready. No messages decoded.",
        RadioMode.Ft8 => $"FT8 clip ready. {decoded!.Count(ch => ch == '\n') + 1} message(s).",
        _ when SpeechDecoder.IsVoice(mode) && !string.IsNullOrWhiteSpace(decoded) => $"Audio clip ready. {decoded}",
        _ => "Audio clip ready."
    };

    private async Task<string> DescribeSourceAsync(double frequencyHz, RadioMode mode, CancellationToken cancellationToken)
    {
        string label = Region2BandPlan.SourceLabel(frequencyHz);
        if (mode != RadioMode.Nbfm || !TryLocation(out double latitude, out double longitude))
            return label;

        try
        {
            string? call = await repeaters.TryFindAsync(frequencyHz, latitude, longitude, cancellationToken);
            return string.IsNullOrWhiteSpace(call) ? label : $"{call} ({label})";
        }
        catch (Exception ex)
        {
            log.LogInformation(ex, "Repeater lookup failed.");
            return label;
        }
    }

    private bool TryLocation(out double latitude, out double longitude)
    {
        if (options.Latitude is not null && options.Longitude is not null)
        {
            latitude = options.Latitude.Value;
            longitude = options.Longitude.Value;
            return true;
        }

        return Maidenhead.TryParse(options.Grid, out latitude, out longitude);
    }

    private IRadioDevice RequireDevice() =>
        _device ?? throw new InvalidOperationException("Open the radio first.");

    private CancellationTokenSource Begin(CancellationToken external)
    {
        lock (_statusGate)
        {
            if (_cancel.IsCancellationRequested)
            {
                _cancel.Dispose();
                _cancel = new CancellationTokenSource();
            }

            return CancellationTokenSource.CreateLinkedTokenSource(external, _cancel.Token);
        }
    }

    private void SetStatus(string phase, double progress, string? message)
    {
        lock (_statusGate)
        {
            _phase = phase;
            _progress = progress;
            _message = message;
        }
    }

    /// <summary>One tuned block of I/Q, plus whether direct sampling and a possible mirror image apply.</summary>
    private sealed record Capture(Complex[] Samples, double CenterHz, double SampleRate, bool DirectSampling, bool RealValued);

    /// <summary>A measured peak together with the samples it was measured in, so a steady local carrier can be told from voice or data.</summary>
    private sealed record Measurement(SpectralPeak Peak, Complex[] Samples, double CenterHz, double SampleRate);
}
