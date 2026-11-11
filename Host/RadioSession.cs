using System.Globalization;
using System.Numerics;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Features;
using AgenticSdr.Dsp;
using AgenticSdr.Radio;

namespace AgenticSdr.Host;

/// <summary>
/// What the page polls a few times a second. Phase is idle, scanning, or listening.
/// Progress runs from 0 to 1. Message is the sentence under the radio name. Error is set when the last operation failed.
/// DeviceIndex is the list position of the radio that is started, and null when the radio is stopped.
/// LiveText is the plain transcript. Transcript is the same text in pieces, so Morse can be colored apart from speech.
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
    string? LiveText,
    IReadOnlyList<TranscriptSpan> Transcript,
    string OperatingMode,
    bool CampActivity,
    double? CampSnrDb,
    CampTune? Camp,
    int CampRevision);

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
    CallsignLookup callsigns,
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
    private readonly List<TranscriptSpan> _transcript = [];
    private string _morseWindow = "";
    private double? _transcriptHz;
    private bool _directSampling;
    private AntennaSpec? _antenna;
    private Complex[] _liveIq = [];
    private readonly object _campGate = new();
    private CampFanout? _fanout;
    private Task? _campTask;
    private string _campKey = "";
    private string _operatingMode = "explore";
    private bool _campActivity;
    private double? _campSnrDb;
    private CampTune? _campTune;
    private int _campRevision;
    private readonly HashSet<string> _announcedCalls = new(StringComparer.OrdinalIgnoreCase);
    private bool _callsignHooks;
    private int _campSampleRate = 48_000;
    private int _campChannels = 1;
    private int _campSquelch = 50;
    private int _radioBorrow;

    /// <summary>True while a scan, guess, inspect, or clip listen holds the radio away from camp.</summary>
    public bool RadioBorrowed => Volatile.Read(ref _radioBorrow) > 0;

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
                    _liveText,
                    _transcript.ToArray(),
                    _operatingMode,
                    _campActivity,
                    _campSnrDb,
                    _campTune,
                    _campRevision);
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

    public bool IsOpen => _device is not null;

    public void PublishCamp(string mode, int revision)
    {
        lock (_statusGate)
        {
            _operatingMode = mode == "camp" ? "camp" : "explore";
            _campRevision = revision;
        }
    }

    /// <summary>0 is open (always hear). 100 is tight. Applies on the next camp audio block.</summary>
    public void SetCampSquelch(int value)
    {
        Volatile.Write(ref _campSquelch, Math.Clamp(value, 0, 100));
    }

    public void NoteCampIdle(string message)
    {
        lock (_statusGate)
        {
            if (_phase == "idle")
                _message = message;
        }
    }

    public void ClearCampNote()
    {
        lock (_statusGate)
        {
            if (_phase == "idle" && _message is not null && _message.StartsWith("Camp", StringComparison.Ordinal))
                _message = _device is null ? "Radio is closed." : "Explore mode.";
        }
    }

    /// <summary>
    /// Tunes the camp target, or releases the radio when there is nothing to camp on.
    /// The same target does not retune. Pages hear the audio through <see cref="TryJoinCamp"/>.
    /// </summary>
    public async Task RetargetCampAsync(CampRequest? request, CancellationToken cancellationToken)
    {
        // A guess or scan has the radio; leave it alone until that work finishes.
        if (RadioBorrowed)
            return;

        string key = request?.Key ?? "";
        Task? previous;
        CampFanout? previousAudio;
        lock (_campGate)
        {
            // Still on this target.
            if (key.Length > 0 && key == _campKey && _campTask is { IsCompleted: false })
                return;
            // Truly idle: nothing to clear and no camp task still holding the radio.
            if (key.Length == 0 && (_campTask is null || _campTask.IsCompleted))
                return;

            previous = _campTask;
            previousAudio = _fanout;
            _fanout = null;
            // Keep _campTask until the old work ends so another clear still waits on it.
            _campKey = "\u0001";
        }

        Stop();
        previousAudio?.Complete();
        if (previous is not null)
        {
            try
            {
                await previous.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }

        lock (_campGate)
        {
            if (ReferenceEquals(_campTask, previous))
                _campTask = null;
            if (_campKey == "\u0001")
                _campKey = "";
        }

        lock (_statusGate)
        {
            _campTune = null;
            _campActivity = false;
            _campSnrDb = null;
        }

        if (request is null || _device is null)
            return;

        var fanout = new CampFanout();
        lock (_campGate)
        {
            _fanout = fanout;
            _campKey = key;
            _campTask = Task.Run(async () =>
            {
                try
                {
                    lock (_statusGate)
                    {
                        _campActivity = false;
                        _campTune = new CampTune(request.EntryId, request.Label, request.FrequencyHz, request.Mode, false, _campSampleRate);
                    }

                    await HoldCampAsync(request, fanout, CancellationToken.None);
                }
                finally
                {
                    fanout.Complete();
                    bool current;
                    lock (_campGate)
                    {
                        current = ReferenceEquals(_fanout, fanout);
                        if (current)
                        {
                            _fanout = null;
                            _campKey = "";
                            _campTask = null;
                        }
                    }

                    if (current)
                    {
                        lock (_statusGate)
                        {
                            _campTune = null;
                            _campActivity = false;
                            _campSnrDb = null;
                        }
                    }
                }
            });
        }
    }

    /// <summary>Stop camp or live audio, then run one radio job. Camp resumes on the next schedule tick.</summary>
    private async Task<T> BorrowRadioAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _radioBorrow);
        try
        {
            Stop();
            await _gate.WaitAsync(cancellationToken);
            try
            {
                return await work(cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _radioBorrow);
        }
    }

    public bool TryJoinCamp(out ChannelReader<byte[]>? reader, out int sampleRate, out int channels, out CampTune? tune)
    {
        lock (_campGate)
        {
            if (_fanout is null || _campTask is not { IsCompleted: false })
            {
                reader = null;
                sampleRate = 48_000;
                channels = 1;
                tune = null;
                return false;
            }

            reader = _fanout.Join();
            sampleRate = _campSampleRate;
            channels = _campChannels;
        }

        lock (_statusGate)
            tune = _campTune;
        return true;
    }

    public void LeaveCamp(ChannelReader<byte[]> reader)
    {
        lock (_campGate)
            _fanout?.Leave(reader);
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
    public Task<AntennaAdvice> GuessAntennaAsync(CancellationToken cancellationToken) =>
        BorrowRadioAsync(async ct =>
        {
            try
            {
                using CancellationTokenSource linked = Begin(ct);
                CancellationToken token = linked.Token;
                IRadioDevice device = RequireDevice();
                List<double> probes = AntennaGuess.ProbesInRange(device.Capabilities.MinFrequencyHz, device.Capabilities.MaxFrequencyHz).ToList();
                if (probes.Count < 3)
                    throw new InvalidOperationException("This radio cannot tune enough of the dial to guess an antenna.");

                double? savedGain = device.Capabilities.GainDb;
                double? probeGain = device.DirectSampling ? 8 : (savedGain is > 30 ? 30 : savedGain);
                if (probeGain != savedGain)
                    device.SetGain(probeGain);
                var readings = new List<SignalStrength>(probes.Count);
                try
                {
                    for (int i = 0; i < probes.Count; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        SetStatus("scanning", i / (double)probes.Count, $"Listening for the antenna near {probes[i] / 1e6:0.00} MHz");
                        Capture capture = await CaptureAsync(device, probes[i], 80, token);
                        double[] db = SpectrumAnalyzer.AveragePowerDb(capture.Samples, 4096);
                        readings.Add(new SignalStrength(probes[i], Percentile(db, 0.15)));
                        log.LogInformation("Antenna floor {MHz:0.00} MHz {Floor:0.0} dB", probes[i] / 1e6, readings[^1].FloorDb);
                    }
                }
                finally
                {
                    if (probeGain != savedGain)
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
        }, cancellationToken);

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

    public RadioReport Details(int index)
    {
        _gate.Wait();
        try
        {
            IReadOnlyList<RadioDeviceInfo> devices = RadioDevices.List();
            if (index < 0 || index >= devices.Count)
                throw new RadioException($"No SDR at index {index}. {devices.Count} device(s) are visible.");
            RadioDeviceInfo info = devices[index];
            if (_device is not null && SameOpenRadio(info))
                return RadioDevices.DescribeOpen(info, _device, options.SampleRateHz);
            if (_device is not null)
                throw new RadioException("Stop the open radio before reading details for a different one.");
            return RadioDevices.Describe(info, options.SampleRateHz);
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool SameOpenRadio(RadioDeviceInfo info)
    {
        if (_deviceIndex != info.Index)
            return false;
        if (string.IsNullOrWhiteSpace(info.Serial) || string.IsNullOrWhiteSpace(_serial))
            return _deviceIndex == info.Index;
        return _serial.Equals(info.Serial, StringComparison.OrdinalIgnoreCase);
    }

    public RadioCapabilities Capabilities()
    {
        IRadioDevice device = _device ?? throw new InvalidOperationException("Open the radio before reading capabilities.");
        return device.Capabilities;
    }

    public Task<ScanReport> ScanAsync(string bandId, CancellationToken cancellationToken)
    {
        HamBand band = Region2BandPlan.Find(bandId)
            ?? throw new ArgumentException($"Unknown band '{bandId}'. Use list_ham_bands for ids such as 2m, 70cm, 40m, fm-bc, am-bc, noaa, cb, gmrs, or dmr.");

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
                        bool digiDial = hint.PreferredMode is RadioMode.Ft8 or RadioMode.Js8 or RadioMode.Olivia or RadioMode.Jtty or RadioMode.Varac;
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
                bool digiHint = hint.PreferredMode is RadioMode.Ft8 or RadioMode.Js8 or RadioMode.Olivia or RadioMode.Jtty or RadioMode.Varac;
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
            bool digiDial = hint.PreferredMode is RadioMode.Ft8 or RadioMode.Js8 or RadioMode.Olivia or RadioMode.Jtty or RadioMode.Varac;
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
            if (requested == RadioMode.Unknown && Region2BandPlan.TryAnyDigiDial(frequencyHz, out RadioMode digiMode, out double digiDial))
            {
                mode = digiMode;
                tuneHz = digiDial;
                // Dial match only — decode success is reported after the listen finishes.
                confidence = 0.45;
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

            if (mode is RadioMode.Ft8 or RadioMode.Js8 or RadioMode.Olivia or RadioMode.Jtty or RadioMode.Varac)
            {
                if (Region2BandPlan.TryDigiDial(tuneHz, mode, out double dial) || Region2BandPlan.TryDigiDial(frequencyHz, mode, out dial))
                    tuneHz = dial;
                seconds = DigiListenSeconds(mode);
                // Leave ~1.2 s to tune and flush junk so capture can start on the UTC boundary.
                if (mode is RadioMode.Ft8 or RadioMode.Js8)
                    await WaitForFt8Slot(ct, earlySeconds: 1.2);
            }

            source = Region2BandPlan.SourceLabel(tuneHz);
            if (TryLocation(out double latitude, out double longitude) && mode == RadioMode.Nbfm)
            {
                string? call = await repeaters.TryFindAsync(tuneHz, latitude, longitude, ct);
                if (!string.IsNullOrWhiteSpace(call))
                    source = $"{call} ({source})";
            }

            SetStatus("listening", 0.35, $"Listening {seconds:0.#} s at {tuneHz / 1e6:0.000000} MHz ({mode}).");
            RadioMode demodMode = mode;
            (double loHz, double shiftHz) = PlaceTuner(device, demodMode, tuneHz);
            EaseWideFmGain(device, demodMode);
            DemodResult result;
            try
            {
                var demod = new Demodulator(demodMode, shiftHz, device.SampleRateHz, demodMode == RadioMode.Wbfm ? wbfmProgram : WbfmProgram.Main);
                await CaptureDemodAsync(device, loHz, seconds, demod, demodMode, ct);
                result = demod.Finish(normalize: true);
            }
            finally
            {
                if (EasedGain(demodMode))
                    RestoreGain(device);
                ReleaseTuner(device);
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
                else if (mode is RadioMode.Ft8 or RadioMode.Js8 or RadioMode.Olivia or RadioMode.Jtty or RadioMode.Varac)
                    AppendDigi(mode, decoded);
                else if (mode == RadioMode.Packet)
                    AppendPacket(decoded);
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
    public Task WriteLiveAsync(double frequencyHz, string? modeName, DmrFilter dmr, HttpResponse response, CancellationToken cancellationToken, string? wbfmProgram = null) =>
        PlayLiveAsync(frequencyHz, modeName, dmr, response, null, false, WbfmPrograms.Parse(wbfmProgram), cancellationToken);

    private Task HoldCampAsync(CampRequest request, CampFanout fanout, CancellationToken cancellationToken) =>
        PlayLiveAsync(request.FrequencyHz, request.Mode, request.Filter, null, fanout, true, WbfmProgram.Main, cancellationToken);

    private async Task PlayLiveAsync(
        double frequencyHz,
        string? modeName,
        DmrFilter dmr,
        HttpResponse? response,
        CampFanout? fanout,
        bool squelch,
        WbfmProgram wbfmProgram,
        CancellationToken cancellationToken)
    {
        RadioMode requested = ModeClassifier.Parse(modeName);
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
            if (requested == RadioMode.Unknown && Region2BandPlan.TryAnyDigiDial(frequencyHz, out RadioMode digiMode, out double digiDialHz))
            {
                mode = digiMode;
                tuneHz = digiDialHz;
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

            if (mode is RadioMode.Ft8 or RadioMode.Js8 or RadioMode.Olivia or RadioMode.Jtty or RadioMode.Varac)
            {
                if (Region2BandPlan.TryDigiDial(tuneHz, mode, out double dial) || Region2BandPlan.TryDigiDial(frequencyHz, mode, out dial))
                    tuneHz = dial;
                // Leave time to open the stream so live digi audio starts on the UTC boundary.
                if (mode is RadioMode.Ft8 or RadioMode.Js8)
                    await WaitForFt8Slot(ct, earlySeconds: 1.2);
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
            RadioMode demodMode = mode;
            (double loHz, double shiftHz) = PlaceTuner(device, demodMode, tuneHz);
            EaseWideFmGain(device, demodMode);
            var demod = new Demodulator(demodMode, shiftHz, device.SampleRateHz, demodMode == RadioMode.Wbfm ? wbfmProgram : WbfmProgram.Main);
            if (response is not null)
            {
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

            try
            {
                await PumpLiveAsync(device, loHz, shiftHz, tuneHz, mode, demod, response?.Body, fanout, squelch, mode == RadioMode.Dmr && !dmrFramed, dmr, ct);
            }
            finally
            {
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
        string digiLog = "";
        Task<string>? speechTask = null;
        Task<(RadioMode Mode, string Text)>? digiDecode = null;
        bool voice = SpeechDecoder.IsVoice(mode);
        bool digi = mode is RadioMode.Ft8 or RadioMode.Js8 or RadioMode.Olivia or RadioMode.Jtty or RadioMode.Varac;
        bool sideband = mode is RadioMode.Usb or RadioMode.Lsb;
        bool hearingMorse = false;
        bool digiHeader = false;
        int rate = demod.PlaybackRate;
        var packetReader = mode == RadioMode.Packet ? new PacketReader(rate) : null;
        using XaraMonitor? xara = mode == RadioMode.Varac ? new XaraMonitor() : null;
        if (mode == RadioMode.Varac && xara is { Available: false })
            AppendNote(xara.MissingHint ?? "VarAC decode needs xara-monitor or zara-monitor beside the app.");
        int remember = voice ? 6 * rate : 8 * rate;
        var nextMorse = DateTime.UtcNow + TimeSpan.FromSeconds(1);
        // Live FT8/JS8 chops on the 15 s protocol period even when a clip listen runs a bit longer.
        int digiSlot = DigiPeriodSeconds(mode) * rate;
        var nextText = DateTime.UtcNow + TimeSpan.FromSeconds(1);
        float level = 0.05f;
        var activity = new ActivityHold();
        bool stereoNoted = false;
        bool sca67Noted = false;
        bool sca92Noted = false;

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
            if (mode is RadioMode.Ft8 or RadioMode.Js8)
                AlignFt8Slot(cancellationToken);
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

            if (mode == RadioMode.Wbfm)
            {
                foreach (string line in demod.TakeRds())
                    AppendRds(line);
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
            }

            if (mode == RadioMode.Cw)
            {
                AppendLimited(recent, fresh, remember);
                if (DateTime.UtcNow >= nextText)
                {
                    string text = MorseDecoder.Decode(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(recent), rate);
                    AppendMorse(text);
                    nextText = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                }
            }
            else if (voice)
            {
                float[] mono = demod.Channels > 1 ? DownmixAudio(fresh, demod.Channels) : fresh;
                AppendLimited(recent, mono, sideband ? 8 * rate : remember);
                if (sideband && DateTime.UtcNow >= nextMorse)
                {
                    string morse = MorseDecoder.DecodeAudible(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(recent), rate);
                    if (MorseDecoder.IsCopy(morse))
                    {
                        hearingMorse = true;
                        speechTask = null;
                        AppendMorse(morse);
                    }
                    else if (hearingMorse)
                    {
                        hearingMorse = false;
                    }

                    nextMorse = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                }

                if (hearingMorse)
                    continue;

                if (speechTask is { IsCompleted: true })
                {
                    try
                    {
                        string said = await speechTask;
                        AppendSpeech(said);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        AppendNote("Speech was not read. " + ex.Message);
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
            else if (mode == RadioMode.Varac && xara is not null)
            {
                if (!xara.Available)
                {
                    if (!digiHeader)
                    {
                        digiHeader = true;
                        AppendNote(xara.MissingHint ?? "VarAC decode needs Host/native/xaramonitor.dll.");
                    }
                }
                else
                {
                    xara.Push(fresh, rate);
                    IReadOnlyList<XaraMessage> heard = xara.Drain();
                    if (heard.Count > 0)
                    {
                        string messages = XaraMonitor.Format(heard);
                        digiLog = string.IsNullOrEmpty(digiLog) ? messages : digiLog + "\n" + messages;
                        AppendDigiLines(mode, messages, ref digiHeader);
                    }
                }
            }
            else if (digi)
            {
                digiPeriod.AddRange(fresh);
                // Decode off the audio loop so a slow FT8 search does not stall the next 15 s of samples.
                if (digiDecode is { IsCompleted: true })
                {
                    try
                    {
                        (RadioMode decodedMode, string messages) = await digiDecode;
                        if (!string.IsNullOrWhiteSpace(messages))
                        {
                            digiLog = string.IsNullOrEmpty(digiLog) ? messages : digiLog + "\n" + messages;
                            AppendDigiLines(decodedMode, messages, ref digiHeader);
                        }
                    }
                    catch (Exception ex)
                    {
                        AppendNote($"Digimode decode failed: {ex.Message}");
                    }

                    digiDecode = null;
                }

                if (digiDecode is null && digiPeriod.Count >= digiSlot)
                {
                    float[] block = digiPeriod.GetRange(0, digiSlot).ToArray();
                    digiPeriod.RemoveRange(0, digiSlot);
                    RadioMode decodeMode = mode;
                    int decodeRate = rate;
                    digiDecode = Task.Run(
                        () => (decodeMode, FormatDigi(decodeMode, block, decodeRate)),
                        cancellationToken);
                }
            }
            else if (mode == RadioMode.Packet && packetReader is not null)
            {
                foreach (string packet in packetReader.Push(fresh, rate))
                {
                    framed = true;
                    AppendPacket(packet);
                }
            }
        }
        }
        finally
        {
            if (digiDecode is { IsCompleted: true })
            {
                try
                {
                    (RadioMode decodedMode, string messages) = digiDecode.GetAwaiter().GetResult();
                    if (!string.IsNullOrWhiteSpace(messages))
                        AppendDigiLines(decodedMode, messages, ref digiHeader);
                }
                catch
                {
                    // Listen is ending; ignore a late decode failure.
                }
            }

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

    private static string LiveMessage(RadioMode mode, double tuneHz) =>
        $"Playing {tuneHz / 1e6:0.000000} MHz ({mode}) as it arrives.";

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
        mode is RadioMode.Wbfm or RadioMode.Nbfm or RadioMode.Dmr or RadioMode.M17 or RadioMode.Packet or RadioMode.Raw;

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
            _transcriptHz = frequencyHz;
        }
    }

    private void AppendMorse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        text = text.Trim();
        lock (_statusGate)
        {
            string shown = _transcript.Count > 0 && _transcript[^1].Kind == "morse" ? _transcript[^1].Text : "";
            string grown = TranscriptLog.Grow(shown, _morseWindow, text).Trim();
            _morseWindow = text;
            if (grown.Length == 0)
                return;
            if (_transcript.Count > 0 && _transcript[^1].Kind == "morse")
                _transcript[^1] = new TranscriptSpan("morse", grown);
            else
                _transcript.Add(new TranscriptSpan("morse", grown));
            PublishTranscript();
        }
    }

    private void AppendSpeech(string text) => AppendSpan("speech", text);

    private void AppendRds(string text) => AppendSpan("rds", text);

    /// <summary>Split a WBFM clip's combined text into RDS lines vs speech.</summary>
    private void AppendWbfmDecoded(string decoded)
    {
        foreach (string raw in decoded.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.StartsWith("RDS ", StringComparison.Ordinal) || raw.StartsWith("Listening for RDS", StringComparison.Ordinal))
                AppendRds(raw);
            else if (raw.StartsWith("Speech was not read", StringComparison.Ordinal))
                AppendNote(raw);
            else
                AppendSpeech(raw);
        }
    }

    private void AppendFt8(string text) => AppendSpan("ft8", text);

    private void AppendDigi(RadioMode mode, string text)
    {
        HookCallsigns();
        callsigns.PrefetchFromText(text);
        AppendSpan(CampSchedule.Canonical(mode), callsigns.EnrichText(text));
    }

    /// <summary>
    /// Print digimode text as separate timed lines. A whole-slot Format() block used to look stuck
    /// when the next slot decoded the same CQ (identical text was dropped by AppendSpan) or when
    /// a slow FT8 search stalled the audio loop so later slots never arrived cleanly.
    /// </summary>
    private void AppendDigiLines(RadioMode mode, string formatted, ref bool headerShown)
    {
        if (string.IsNullOrWhiteSpace(formatted))
            return;

        HookCallsigns();
        callsigns.PrefetchFromText(formatted);
        string kind = CampSchedule.Canonical(mode);
        string stamp = DateTime.UtcNow.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        string[] lines = formatted.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        string? pending = null;
        foreach (string raw in lines)
        {
            string line = raw.TrimEnd();
            if (line.Length == 0)
                continue;

            bool banner = line is "FT8" or "JS8" or "Olivia" or "JTTY" or "VarAC"
                || line.StartsWith("FT8 ", StringComparison.Ordinal)
                || line.StartsWith("JS8 ", StringComparison.Ordinal)
                || line.StartsWith("Olivia ", StringComparison.Ordinal)
                || line.StartsWith("JTTY ", StringComparison.Ordinal)
                || line.StartsWith("VarAC ", StringComparison.Ordinal);
            if (banner)
            {
                FlushDigiLine(kind, ref pending);
                if (headerShown)
                    continue;
                headerShown = true;
                AppendSpan(kind, line);
                continue;
            }

            // Indented explanation stays on the same transcript span as the message above.
            if (raw.Length > 0 && char.IsWhiteSpace(raw[0]))
            {
                string meaning = callsigns.EnrichText(line.TrimStart());
                if (pending is null)
                    pending = $"{stamp}  {meaning}";
                else
                    pending += "\n           " + meaning;
                continue;
            }

            FlushDigiLine(kind, ref pending);
            pending = $"{stamp}  {callsigns.EnrichText(line)}";
        }

        FlushDigiLine(kind, ref pending);
    }

    private void FlushDigiLine(string kind, ref string? pending)
    {
        if (pending is null)
            return;
        AppendSpan(kind, pending);
        pending = null;
    }

    private void HookCallsigns()
    {
        if (_callsignHooks || !callsigns.Enabled)
            return;
        _callsignHooks = true;
        callsigns.Resolved += OnCallsignResolved;
    }

    private void OnCallsignResolved(CallsignInfo info)
    {
        string? note = info.Note ?? info.Label;
        if (!info.Found || string.IsNullOrWhiteSpace(note))
            return;
        lock (_statusGate)
        {
            if (!_announcedCalls.Add(info.Call))
                return;
        }

        AppendNote($"{info.Call} — {note}");
    }

    private static string FormatDigi(RadioMode mode, float[] audio, int rate) => mode switch
    {
        RadioMode.Ft8 => Ft8Decoder.Format(Ft8Decoder.Decode(audio, rate)),
        RadioMode.Js8 => Js8Decoder.Format(Js8Decoder.Decode(audio, rate)),
        RadioMode.Olivia => OliviaDecoder.Format(OliviaDecoder.Decode(audio, rate)),
        RadioMode.Jtty => JttyDecoder.Format(JttyDecoder.Decode(audio, rate)),
        RadioMode.Varac => XaraMonitor.FormatAudio(audio, rate),
        _ => ""
    };

    /// <summary>Clip length. FT8/JS8 runs a little past 15 s so a late start still covers one full message.</summary>
    private static int DigiListenSeconds(RadioMode mode) => mode switch
    {
        RadioMode.Ft8 or RadioMode.Js8 => 16,
        RadioMode.Olivia => 20,
        RadioMode.Jtty => 10,
        RadioMode.Varac => 20,
        _ => 15
    };

    /// <summary>Live digi chop length — the protocol period, not the clip pad.</summary>
    private static int DigiPeriodSeconds(RadioMode mode) => mode switch
    {
        RadioMode.Ft8 or RadioMode.Js8 => 15,
        _ => DigiListenSeconds(mode)
    };

    private void AppendPacket(string text) => AppendSpan("packet", text);

    private void AppendNote(string text) => AppendSpan("note", text);

    private void AppendSpan(string kind, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        text = text.Trim();
        lock (_statusGate)
        {
            if (_transcript.Count > 0 && _transcript[^1].Kind == kind && _transcript[^1].Text == text)
                return;
            _transcript.Add(new TranscriptSpan(kind, text));
            PublishTranscript();
        }
    }

    private void PublishTranscript()
    {
        const int maxChars = 12_000;
        int total = _transcript.Sum(span => span.Text.Length + 1);
        while (_transcript.Count > 1 && total > maxChars)
        {
            total -= _transcript[0].Text.Length + 1;
            _transcript.RemoveAt(0);
        }

        _liveText = _transcript.Count == 0 ? null : string.Join('\n', _transcript.Select(span => span.Text));
    }

    /// <summary>
    /// Digimodes are weak multi-tone USB windows above published dials. Speech scan misses that comb, so each dial is probed on its own.
    /// </summary>
    private async Task ProbeDigiDialsAsync(
        IRadioDevice device,
        HamBand band,
        List<ScanHit> hits,
        CancellationToken cancellationToken)
    {
        foreach (CallingChannel channel in band.Calling)
        {
            if (channel.Mode is not (RadioMode.Ft8 or RadioMode.Js8 or RadioMode.Olivia or RadioMode.Jtty or RadioMode.Varac))
                continue;
            if (channel.FrequencyHz < device.Capabilities.MinFrequencyHz
                || channel.FrequencyHz > device.Capabilities.MaxFrequencyHz)
                continue;

            cancellationToken.ThrowIfCancellationRequested();
            string label = CampSchedule.Canonical(channel.Mode).ToUpperInvariant();
            SetStatus(
                "scanning",
                0.92,
                $"Checking {label} at {channel.FrequencyHz / 1e6:0.000} MHz");

            double offset = Math.Min(50_000, device.SampleRateHz * 0.2);
            double tune = Math.Clamp(
                channel.FrequencyHz + offset,
                device.Capabilities.MinFrequencyHz,
                device.Capabilities.MaxFrequencyHz);
            // Fine FFT so narrow digi windows are many bins.
            Capture capture = await CaptureAsync(device, tune, 350, cancellationToken);
            int fft = capture.Samples.Length >= 131_072 ? 65_536 : 16_384;
            double[] db = SpectrumAnalyzer.AveragePowerDb(capture.Samples, fft);
            Ft8Score score = DigiActivity.Score(
                channel.Mode,
                db,
                capture.CenterHz,
                capture.SampleRate,
                channel.FrequencyHz,
                channel.ToleranceHz);
            if (!score.Active)
                continue;

            int existing = hits.FindIndex(hit => NearDigiDial(hit.FrequencyHz, channel.Mode, channel.FrequencyHz, channel.ToleranceHz));
            double width = channel.Mode switch
            {
                RadioMode.Olivia => OliviaDecoder.BandwidthHz,
                RadioMode.Jtty => JttyDecoder.SpanHz,
                RadioMode.Varac => XaraMonitor.BandwidthHz,
                _ => 3_000
            };
            var hit = new ScanHit(
                channel.FrequencyHz,
                channel.Mode.ToString(),
                DigiActivity.ActivityConfidence(score),
                width,
                Math.Round(score.SnrDb, 1),
                channel.Name + " (activity)",
                band.Id);
            if (existing >= 0)
                hits[existing] = hit;
            else
                hits.Add(hit);
        }
    }

    private static bool NearDigiDial(double frequencyHz, RadioMode mode, double dialHz, double toleranceHz)
    {
        if (Region2BandPlan.TryDigiDial(frequencyHz, mode, out double matched) && Math.Abs(matched - dialHz) < 1)
            return true;
        if (mode is RadioMode.Ft8 or RadioMode.Js8 or RadioMode.Varac)
            return frequencyHz >= dialHz - 500 && frequencyHz <= dialHz + toleranceHz;
        return Math.Abs(frequencyHz - dialHz) <= toleranceHz;
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

    private async Task<(List<double[]> Frames, bool DirectSampling, double CenterHz, double SampleRate)> CaptureSpectraAsync(
        IRadioDevice device,
        double frequencyHz,
        int dwellMs,
        Action<int, int> onSlice,
        CancellationToken cancellationToken)
    {
        int sliceMs = HopPlanner.SliceMilliseconds;
        int slices = Math.Max(1, dwellMs / sliceMs);
        int stride = device.Encoding == SampleEncoding.Cu8 ? 2 : 8;
        int unit = Math.Max(1, device.ReadAlignment / stride);
        int samples = Math.Max(unit, (int)(device.SampleRateHz * sliceMs / 1000.0));
        samples = Math.Max(unit, samples / unit * unit);
        var buffer = new byte[samples * stride];
        var frames = new List<double[]>(slices);
        bool direct = false;
        double centerHz = frequencyHz;
        double sampleRate = device.SampleRateHz;
        var chunk = new byte[Aligned(device, 262_144)];
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            device.Tune(frequencyHz);
            lock (_statusGate)
                _directSampling = device.DirectSampling;
            direct = device.DirectSampling;
            centerHz = device.CenterFrequencyHz;
            sampleRate = device.SampleRateHz;
            var junk = new byte[Aligned(device, 32 * 1024)];
            device.Read(junk);
            for (int slice = 0; slice < slices; slice++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                onSlice(slice, slices);
                int got = 0;
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

                (Complex[] iq, _) = ToIq(device, buffer);
                frames.Add(SpectrumAnalyzer.AveragePowerDb(iq, 4096));
            }
        }, cancellationToken);

        return (frames, direct, centerHz, sampleRate);
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
            // Align after junk so the first Costas symbols are not clipped by tuner settle.
            if (mode is RadioMode.Ft8 or RadioMode.Js8)
                AlignFt8Slot(cancellationToken);
            // One Hilbert stream for the whole listen — per-chunk FIR resets destroy FT8 Costas.
            Iq.DirectSamplingState? ds = device.DirectSampling ? new Iq.DirectSamplingState() : null;
            long got = 0;
            while (got < target)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int n = device.Read(chunk);
                if (n <= 0)
                    throw new RadioException("The radio returned no samples.");
                int usable = n / stride * stride;
                (Complex[] iq, _) = ToIq(device, chunk.AsSpan(0, usable), ds);
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

    private static (Complex[] Samples, bool RealValued) ToIq(
        IRadioDevice device,
        ReadOnlySpan<byte> bytes,
        Iq.DirectSamplingState? directSampling = null)
    {
        if (device.Encoding == SampleEncoding.Cu8)
            return Iq.Convert(bytes, device.DirectSampling, directSampling);

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

    /// <summary>
    /// Wait until just before the next 15 s UTC digimode boundary.
    /// <paramref name="earlySeconds"/> leaves room for tuner setup before the exact align.
    /// </summary>
    private async Task WaitForFt8Slot(CancellationToken cancellationToken, double earlySeconds = 0)
    {
        double into = DateTime.UtcNow.TimeOfDay.TotalSeconds % 15.0;
        double untilBoundary = into < 0.15 ? 0 : 15.0 - into;
        double wait = Math.Max(0, untilBoundary - Math.Max(0, earlySeconds));
        if (wait <= 0.05)
            return;

        var started = DateTime.UtcNow;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double left = wait - (DateTime.UtcNow - started).TotalSeconds;
            if (left <= 0.05)
                break;
            SetStatus("listening", 0.05 + 0.25 * (1 - Math.Clamp(left / wait, 0, 1)), $"Waiting {Math.Ceiling(left):0} s for the next 15 s digimode slot.");
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(250, Math.Max(20, left * 1000))), cancellationToken);
        }
    }

    /// <summary>Block until the UTC 15 s boundary so FT8 audio starts with the Costas sync.</summary>
    private static void AlignFt8Slot(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double into = DateTime.UtcNow.TimeOfDay.TotalSeconds % 15.0;
            if (into < 0.12 || into > 14.95)
                return;
            int sleepMs = (int)Math.Clamp((15.0 - into) * 1000.0, 5, 40);
            Thread.Sleep(sleepMs);
        }
    }

    private static string ReadyMessage(RadioMode mode, string? decoded) => mode switch
    {
        RadioMode.Cw when string.IsNullOrWhiteSpace(decoded) => "CW clip ready.",
        RadioMode.Cw => $"CW clip ready: {decoded}",
        RadioMode.Ft8 when string.IsNullOrWhiteSpace(decoded) => "FT8 clip ready. No messages decoded.",
        RadioMode.Ft8 => $"FT8 clip ready. {decoded!.Count(ch => ch == '\n') + 1} message(s).",
        RadioMode.Js8 when string.IsNullOrWhiteSpace(decoded) => "JS8 clip ready. No messages decoded.",
        RadioMode.Js8 => $"JS8 clip ready. {decoded!.Count(ch => ch == '\n') + 1} message(s).",
        RadioMode.Olivia when string.IsNullOrWhiteSpace(decoded) => "Olivia clip ready. No text decoded.",
        RadioMode.Olivia => $"Olivia clip ready. {decoded!.Count(ch => ch == '\n') + 1} line(s).",
        RadioMode.Jtty when string.IsNullOrWhiteSpace(decoded) => "JTTY clip ready. No text decoded.",
        RadioMode.Jtty => $"JTTY clip ready. {decoded!.Count(ch => ch == '\n') + 1} line(s).",
        RadioMode.Varac when string.IsNullOrWhiteSpace(decoded) => "VarAC clip ready. No text decoded (xara/zara monitor may be missing).",
        RadioMode.Varac => $"VarAC clip ready. {decoded!.Count(ch => ch == '\n') + 1} line(s).",
        RadioMode.Packet when string.IsNullOrWhiteSpace(decoded) => "Packet clip ready. No frames decoded.",
        RadioMode.Packet => $"Packet clip ready. {decoded!.Count(ch => ch == '\n') + 1} frame(s).",
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
