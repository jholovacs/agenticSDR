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
    public async Task OpenAsync(int index, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _device?.Dispose();
            _device = null;
            (IRadioDevice device, RadioDeviceInfo info) = await Task.Run(() =>
            {
                IReadOnlyList<RadioDeviceInfo> devices = RadioDevices.List();
                if (index < 0 || index >= devices.Count)
                    throw new RadioException($"No SDR at index {index}. {devices.Count} device(s) are visible.");
                RadioDeviceInfo chosen = devices[index];
                int ppm = radioCorrections.Resolve(chosen, options.Ppm);
                IRadioDevice opened = RadioDevices.Open(chosen, options.SampleRateHz, options.AutoGain ? null : options.GainDb, ppm);
                return (opened, chosen);
            }, cancellationToken);
            _device = device;
            radioPreference.Save(info);
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

    public object RadioPreferenceView()
    {
        IReadOnlyList<RadioDeviceInfo> devices = RadioDevices.List();
        int index = radioPreference.ResolveIndex(devices);
        RadioPreference? saved = radioPreference.Load();
        return new
        {
            index,
            serial = saved?.Serial,
            driver = saved?.Driver,
            name = saved?.Name ?? (index < devices.Count ? devices[index].Name : null),
            simulateTransmit = saved?.SimulateTransmit ?? false
        };
    }

    public object ListDevicesView()
    {
        IReadOnlyList<RadioDeviceInfo> devices = RadioDevices.List();
        return devices.Select(DeviceView).ToArray();
    }

    public object PpmView(int index)
    {
        RadioDeviceInfo info = RequireDevice(index);
        int? saved = radioCorrections.Get(info);
        int ppm = saved ?? options.Ppm;
        return new
        {
            index,
            key = RadioIdentity.Key(info),
            name = info.Name,
            serial = info.Serial,
            driver = info.Driver,
            ppm,
            saved = saved is not null,
            fallbackPpm = options.Ppm
        };
    }

    public object SetPpm(int index, int ppm)
    {
        _gate.Wait();
        try
        {
            RadioDeviceInfo info = RequireDevice(index);
            RadioCorrection entry = radioCorrections.Set(info, ppm);
            if (_device is not null && SameOpenRadio(info))
                _device.SetFrequencyCorrection(entry.Ppm);
            return PpmView(index);
        }
        finally
        {
            _gate.Release();
        }
    }

    private RadioDeviceInfo RequireDevice(int index)
    {
        IReadOnlyList<RadioDeviceInfo> devices = RadioDevices.List();
        if (index < 0 || index >= devices.Count)
            throw new RadioException($"No SDR at index {index}. {devices.Count} device(s) are visible.");
        return devices[index];
    }

    private object DeviceView(RadioDeviceInfo info) => new
    {
        index = info.Index,
        name = info.Name,
        manufacturer = info.Manufacturer,
        product = info.Product,
        serial = info.Serial,
        driver = info.Driver,
        driverIndex = info.DriverIndex,
        openArgs = info.OpenArgs,
        key = RadioIdentity.Key(info),
        ppm = radioCorrections.Resolve(info, options.Ppm)
    };

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
            RadioReport report;
            if (_device is not null && SameOpenRadio(info))
                report = RadioDevices.DescribeOpen(info, _device, options.SampleRateHz);
            else if (_device is not null)
                throw new RadioException("Stop the open radio before reading details for a different one.");
            else
                report = RadioDevices.Describe(info, options.SampleRateHz);

            int ppm = radioCorrections.Resolve(info, options.Ppm);
            var facts = report.Facts.ToList();
            bool hasClock = facts.Exists(f => f.Label.Equals("Clock correction", StringComparison.OrdinalIgnoreCase));
            if (hasClock)
            {
                for (int i = 0; i < facts.Count; i++)
                {
                    if (facts[i].Label.Equals("Clock correction", StringComparison.OrdinalIgnoreCase))
                        facts[i] = new RadioFact("Clock correction", $"{ppm} ppm");
                }
            }
            else
                facts.Add(new RadioFact("Clock correction", $"{ppm} ppm"));
            return new RadioReport(report.Title, facts);
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

    /// <summary>
    /// Measures published NOAA carriers with ppm cleared, estimates crystal error, persists it for this radio,
    /// and applies it to the open device so future tunes stay on frequency.
    /// </summary>
    public Task<object> CalibrateAsync(CancellationToken cancellationToken) =>
        BorrowRadioAsync(async ct =>
        {
            using CancellationTokenSource linked = Begin(ct);
            CancellationToken token = linked.Token;
            IRadioDevice device = RequireDevice();
            if (_deviceIndex is null)
                throw new InvalidOperationException("Open a radio before calibrating.");

            RadioDeviceInfo info = RequireDevice(_deviceIndex.Value);
            int previousPpm = device.Capabilities.Ppm;
            device.SetFrequencyCorrection(0);

            try
            {
                List<CallingChannel> references = Region2BandPlan.CalibrationReferences()
                    .Where(channel =>
                        channel.FrequencyHz >= device.Capabilities.MinFrequencyHz
                        && channel.FrequencyHz <= device.Capabilities.MaxFrequencyHz)
                    .ToList();
                if (references.Count == 0)
                    throw new InvalidOperationException("No calibration carriers fall inside this radio's tuning range.");

                var observations = new List<PpmObservation>();
                for (int i = 0; i < references.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    CallingChannel channel = references[i];
                    SetStatus(
                        "calibrating",
                        (i + 0.2) / references.Count,
                        $"Calibrating on {channel.Name} ({channel.FrequencyHz / 1e6:0.000} MHz)…");

                    PpmObservation? hit = await MeasureCalibrationAsync(device, channel, token);
                    if (hit is null)
                        continue;
                    observations.Add(hit);
                    log.LogInformation(
                        "Calibration {Name}: true {True:0} Hz measured {Measured:0} Hz → {Ppm:0.0} ppm (SNR {Snr:0.0} dB)",
                        hit.Name, hit.TrueHz, hit.MeasuredHz, hit.Ppm, hit.SnrDb);
                }

                if (observations.Count == 0)
                {
                    device.SetFrequencyCorrection(previousPpm);
                    throw new InvalidOperationException(
                        "No known carriers were heard. Check the antenna outdoors near a NOAA weather transmitter, then try Calibrate again.");
                }

                int ppm = Math.Clamp(
                    RadioCalibration.CombinePpm(observations),
                    RadioCorrectionStore.MinPpm,
                    RadioCorrectionStore.MaxPpm);
                radioCorrections.Set(info, ppm);
                device.SetFrequencyCorrection(ppm);

                string summary = observations.Count == 1
                    ? $"Calibrated to {ppm} ppm from {observations[0].Name}."
                    : $"Calibrated to {ppm} ppm from {observations.Count} NOAA carriers.";
                SetStatus("idle", 1, summary);

                return (object)new
                {
                    ppm,
                    previousPpm,
                    saved = true,
                    index = info.Index,
                    key = RadioIdentity.Key(info),
                    name = info.Name,
                    serial = info.Serial,
                    message = summary,
                    observations = observations.Select(o => new
                    {
                        name = o.Name,
                        trueHz = o.TrueHz,
                        measuredHz = o.MeasuredHz,
                        offsetHz = Math.Round(o.MeasuredHz - o.TrueHz, 1),
                        snrDb = Math.Round(o.SnrDb, 1),
                        ppm = Math.Round(o.Ppm, 2)
                    }).ToArray()
                };
            }
            catch (OperationCanceledException)
            {
                try { device.SetFrequencyCorrection(previousPpm); } catch { /* best effort */ }
                SetStatus("idle", 0, "Calibration stopped.");
                throw;
            }
            catch
            {
                try { device.SetFrequencyCorrection(previousPpm); } catch { /* best effort */ }
                throw;
            }
        }, cancellationToken);

    private async Task<PpmObservation?> MeasureCalibrationAsync(
        IRadioDevice device,
        CallingChannel channel,
        CancellationToken cancellationToken)
    {
        double trueHz = channel.FrequencyHz;
        double searchHz = RadioCalibration.SearchHz(trueHz);
        double offset = Math.Min(50_000, device.SampleRateHz * 0.2);
        double captureHz = Math.Clamp(
            trueHz + offset,
            device.Capabilities.MinFrequencyHz,
            device.Capabilities.MaxFrequencyHz);

        Capture capture = await CaptureAsync(device, captureHz, 220, cancellationToken);
        int fft = capture.Samples.Length >= 65_536 ? 16_384 : 8_192;
        double[] db = SpectrumAnalyzer.AveragePowerDb(capture.Samples, fft);
        SpectralPeak? peak = PeakPicker.LoudestIn(
            db, capture.CenterHz, capture.SampleRate, trueHz, searchHz, minSnrDb: 8);
        if (peak is null)
            return null;

        double ppm = RadioCalibration.PpmFromMeasured(trueHz, peak.FrequencyHz);
        if (Math.Abs(ppm) > RadioCorrectionStore.MaxPpm)
            return null;

        return new PpmObservation(channel.Name, trueHz, peak.FrequencyHz, peak.SnrDb, ppm);
    }
}
