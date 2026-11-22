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

/// <summary>
/// What the page polls a few times a second. Phase is idle, scanning, or listening.
/// Progress runs from 0 to 1. Message is the sentence under the radio name. Error is set when the last operation failed.
/// DeviceIndex is the list position of the radio that is started, and null when the radio is stopped.
/// LiveText is the plain transcript. Transcript is the same text in pieces, so Morse can be colored apart from speech.
/// Pipeline is the ordered filters/decoders for the current listen.
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
    IReadOnlyList<PipelineStageInfo> Pipeline,
    string OperatingMode,
    bool CampActivity,
    double? CampSnrDb,
    CampTune? Camp,
    int CampRevision,
    IReadOnlyList<AprsWeatherStation> WeatherStations,
    double? MapLatitude,
    double? MapLongitude);

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

/// <summary>One ranked mode candidate from Signal Analyze.</summary>
public sealed record AnalyzeCandidate(
    string Mode,
    double Confidence,
    double TuneHz,
    double BandwidthHz,
    string Reason,
    bool DecodeConfirmed);

/// <summary>
/// Result of carefully re-scoring a Signals hit: recommended tune/mode/bandwidth and why.
/// Changed is true when the recommendation differs from the prior scan guess.
/// </summary>
public sealed record AnalyzeReport(
    double FrequencyHz,
    string? PriorMode,
    string RecommendedMode,
    double RecommendedTuneHz,
    double RecommendedBandwidthHz,
    double Confidence,
    double SnrDb,
    string Explanation,
    bool Changed,
    IReadOnlyList<AnalyzeCandidate> Candidates);

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
public sealed partial class RadioSession(
    SdrOptions options,
    ClipStore clips,
    RepeaterLookup repeaters,
    CallsignLookup callsigns,
    SpeechDecoder speech,
    ProcessorCatalog processors,
    AntennaStore antennas,
    RadioPreferenceStore radioPreference,
    RadioCorrectionStore radioCorrections,
    UiPrefsStore uiPrefs,
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
    private PipelineStageInfo[] _pipeline = [];
    private readonly object _liveChainGate = new();
    private SignalChain? _liveChain;
    private RadioMode _liveMode = RadioMode.Unknown;
    private string _morseWindow = "";
    private double? _transcriptHz;
    private bool _directSampling;
    private AntennaSpec? _antenna = antennas.Load();
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
    private string _lastLiveDigiBody = "";
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, AprsWeatherStation> _weatherStations =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (double Lat, double Lon)> _aprsPositions =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan WeatherStationTtl = TimeSpan.FromMinutes(60);

    /// <summary>True while a scan, guess, inspect, or clip listen holds the radio away from camp.</summary>
    public bool RadioBorrowed => Volatile.Read(ref _radioBorrow) > 0;

    public SessionStatus Status
    {
        get
        {
            lock (_statusGate)
            {
                bool haveMap = TryLocation(out double mapLat, out double mapLon);
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
                    _pipeline,
                    _operatingMode,
                    _campActivity,
                    _campSnrDb,
                    _campTune,
                    _campRevision,
                    SnapshotWeatherStations(),
                    haveMap ? mapLat : null,
                    haveMap ? mapLon : null);
            }
        }
    }

    public ProcessorCatalog Processors => processors;

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
        antennas.Save(_antenna);
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
                antennas.Save(spec);
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
}
