using AgenticSdr.Privilege;
using AgenticSdr.Shared;
using AgenticSdr.Radio;

namespace AgenticSdr.Cw.Listen;

/// <summary>USB radio held by the CW desk. Preference and ppm corrections are shared with Host.</summary>
public sealed class CwRadioHolder : IDisposable
{
    readonly object _gate = new();
    readonly RadioPreferenceStore _preference;
    readonly RadioCorrectionStore _corrections;
    readonly CwListener _listener;
    readonly Dictionary<string, byte[]> _previews = new();
    IRadioDevice? _raw;
    IRadioDevice? _radio;
    int? _index;
    string? _name;
    string? _serial;
    bool _simulateTransmit;

    public CwRadioHolder(RadioPreferenceStore preference, RadioCorrectionStore corrections, CwListener listener)
    {
        _preference = preference;
        _corrections = corrections;
        _listener = listener;
        _simulateTransmit = preference.SimulateTransmit();
    }

    public IRadioDevice? Radio
    {
        get
        {
            lock (_gate)
                return _radio;
        }
    }

    public bool SimulateTransmit
    {
        get
        {
            lock (_gate)
                return _simulateTransmit;
        }
    }

    public object Status()
    {
        CwListener.ListenStatus listen = _listener.Status();
        lock (_gate)
        {
            bool canTransmit = _radio?.CanTransmit ?? false;
            return new
            {
                deviceOpen = _radio is not null,
                open = _radio is not null,
                deviceName = _name,
                name = _name,
                serial = _serial,
                deviceIndex = _index,
                canTransmit,
                simulateTransmit = _simulateTransmit,
                centerHz = _radio?.CenterFrequencyHz,
                listening = listen.Listening,
                listenHz = listen.FrequencyHz,
                listenSampleRate = listen.SampleRate,
                listenError = listen.Error
            };
        }
    }

    public IReadOnlyList<RadioDeviceInfo> ListDevices() => RadioDevices.List();

    public object ListDevicesView()
    {
        return ListDevices().Select(info => new
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
            ppm = _corrections.Resolve(info, fallbackPpm: 0)
        }).ToArray();
    }

    public object Preference()
    {
        IReadOnlyList<RadioDeviceInfo> devices = ListDevices();
        int index = _preference.ResolveIndex(devices);
        RadioPreference? saved = _preference.Load();
        return new
        {
            index,
            serial = saved?.Serial,
            driver = saved?.Driver,
            name = saved?.Name ?? (index < devices.Count ? devices[index].Name : null),
            simulateTransmit = saved?.SimulateTransmit ?? _simulateTransmit
        };
    }

    public object PpmView(int index)
    {
        RadioDeviceInfo info = RequireDevice(index);
        int? saved = _corrections.Get(info);
        int ppm = saved ?? 0;
        return new
        {
            index,
            key = RadioIdentity.Key(info),
            name = info.Name,
            serial = info.Serial,
            driver = info.Driver,
            ppm,
            saved = saved is not null,
            fallbackPpm = 0
        };
    }

    public object SetPpm(int index, int ppm)
    {
        lock (_gate)
        {
            RadioDeviceInfo info = RequireDevice(index);
            RadioCorrection entry = _corrections.Set(info, ppm);
            if (_raw is not null && _index == index)
                _raw.SetFrequencyCorrection(entry.Ppm);
            return PpmView(index);
        }
    }

    RadioDeviceInfo RequireDevice(int index)
    {
        IReadOnlyList<RadioDeviceInfo> devices = ListDevices();
        if (index < 0 || index >= devices.Count)
            throw new RadioException($"No SDR at index {index}. {devices.Count} device(s) are visible.");
        return devices[index];
    }

    public object SetSimulateTransmit(bool enabled)
    {
        lock (_gate)
        {
            _simulateTransmit = enabled;
            _preference.SetSimulateTransmit(enabled);
            ApplyFacadeUnlocked();
        }

        return Status();
    }

    public object Open(int index)
    {
        lock (_gate)
        {
            IReadOnlyList<RadioDeviceInfo> devices = RadioDevices.List();
            if (index < 0 || index >= devices.Count)
                throw new RadioException($"No SDR at index {index}. {devices.Count} device(s) are visible.");

            RadioDeviceInfo info = devices[index];
            _listener.PauseForTransmit();
            _raw?.Dispose();
            int ppm = _corrections.Resolve(info, fallbackPpm: 0);
            _raw = RadioDevices.Open(info, sampleRateHz: RadioLimits.DefaultSampleRateHz, gainDb: null, ppm: ppm);
            _index = index;
            _name = _raw.Capabilities.Name;
            _serial = _raw.Capabilities.Serial;
            _simulateTransmit = _preference.SimulateTransmit();
            ApplyFacadeUnlocked();
            _preference.Save(info);
        }

        // Listening starts when the operator clicks Listen on the CW chat dial.
        return Status();
    }

    public object Close()
    {
        _listener.Stop();
        lock (_gate)
        {
            _raw?.Dispose();
            _raw = null;
            _radio = null;
            _index = null;
            _name = null;
            _serial = null;
        }

        return Status();
    }

    public RadioReport Details(int index)
    {
        IReadOnlyList<RadioDeviceInfo> devices = RadioDevices.List();
        if (index < 0 || index >= devices.Count)
            throw new RadioException($"No SDR at index {index}.");

        lock (_gate)
        {
            if (_raw is not null && _index == index)
                return RadioDevices.DescribeOpen(devices[index], _raw, RadioLimits.DefaultSampleRateHz);
        }

        return RadioDevices.Describe(devices[index], RadioLimits.DefaultSampleRateHz);
    }

    public void StorePreview(string id, byte[] wav)
    {
        lock (_gate)
            _previews[id] = wav;
    }

    public bool TryTakePreview(string id, out byte[]? wav)
    {
        lock (_gate)
        {
            if (_previews.Remove(id, out byte[]? found))
            {
                wav = found;
                return true;
            }
        }

        wav = null;
        return false;
    }

    public void Dispose() => Close();

    void ApplyFacadeUnlocked()
    {
        if (_raw is null)
        {
            _radio = null;
            return;
        }

        _radio = _simulateTransmit ? new SimulatedTransmitRadio(_raw) : _raw;
    }
}
