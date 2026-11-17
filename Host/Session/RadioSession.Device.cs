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
                IRadioDevice opened = RadioDevices.Open(chosen, options.SampleRateHz, options.AutoGain ? null : options.GainDb, options.Ppm);
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


}
