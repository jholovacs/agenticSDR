using System.Runtime.InteropServices;

namespace AgenticSdr.Radio;

/// <summary>
/// An open LimeSDR through LimeSuiteNG. Only the receive channel is enabled.
/// The transmit ports are set to None and no transmit stream is created.
/// </summary>
public sealed class LimeDevice : IRadioDevice
{
    private readonly nuint _channel;
    private readonly string? _forcedPortName;
    private readonly IntPtr _device;
    private readonly List<LimePlan.Port> _ports;
    private LmsStream _stream;
    private bool _streaming;
    private bool _disposed;
    private double _filterHz;
    private int _port = -1;
    private double _calibratedHz;

    private LimeDevice(IntPtr device, RadioCapabilities capabilities, List<LimePlan.Port> ports, LmsStream stream, double filterHz, nuint channel, string? forcedPortName)
    {
        _device = device;
        _ports = ports;
        _stream = stream;
        _streaming = true;
        _filterHz = filterHz;
        _channel = channel;
        _forcedPortName = forcedPortName;
        Capabilities = capabilities;
        SampleRateHz = capabilities.SampleRateHz;
    }

    public RadioCapabilities Capabilities { get; }

    public bool DirectSampling => false;

    public double CenterFrequencyHz { get; private set; }

    public double SampleRateHz { get; }

    public SampleEncoding Encoding => SampleEncoding.Cf32;

    public int ReadAlignment => 8;

    public static IReadOnlyList<RadioDeviceInfo> TryList()
    {
        try
        {
            return LimeCall.Run(ListDevices);
        }
        catch (DllNotFoundException)
        {
            return [];
        }
        catch (EntryPointNotFoundException)
        {
            return [];
        }
        catch (BadImageFormatException)
        {
            return [];
        }
    }

    private static IReadOnlyList<RadioDeviceInfo> ListDevices()
    {
        try
        {
            LimeNative.Touch();
            int count = LimeNative.LMS_GetDeviceList(IntPtr.Zero);
            if (count <= 0)
                return [];

            IntPtr buffer = Marshal.AllocHGlobal(count * 256);
            try
            {
                int written = LimeNative.LMS_GetDeviceList(buffer);
                var found = new List<RadioDeviceInfo>();
                for (int i = 0; i < written; i++)
                {
                    string text = Marshal.PtrToStringAnsi(buffer + i * 256) ?? "";
                    if (string.IsNullOrWhiteSpace(text))
                        continue;
                    (string name, string? serial) = Describe(text);
                    string baseName = SdrCatalog.DisplayName("lime", name);
                    bool isMini = name.Contains("mini", StringComparison.OrdinalIgnoreCase);
                    int channelCount = isMini ? 1 : 2;

                    for (int ch = 0; ch < channelCount; ch++)
                    {
                        string chLabel = $"RX{ch + 1}";
                        var portConfigs = isMini
                            ? new[] { ("LNAW", $"{baseName} — {chLabel} Wideband ({chLabel}_W)") }
                            : new[]
                            {
                                ("LNAW", $"{baseName} — {chLabel} Wideband ({chLabel}_W)"),
                                ("LNAL", $"{baseName} — {chLabel} Low ({chLabel}_L)"),
                                ("LNAH", $"{baseName} — {chLabel} High ({chLabel}_H)")
                            };

                        foreach (var (portKey, displayName) in portConfigs)
                        {
                            string openArgs = $"{text}|channel={ch}|port={portKey}";
                            found.Add(new RadioDeviceInfo(
                                found.Count,
                                displayName,
                                "MyriadRF",
                                name,
                                serial,
                                "lime",
                                found.Count,
                                openArgs));
                        }
                    }
                }

                return found;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (DllNotFoundException)
        {
            return [];
        }
        catch (EntryPointNotFoundException)
        {
            return [];
        }
        catch (BadImageFormatException)
        {
            return [];
        }
    }

    public static LimeDevice Open(string args, double preferredRate, double? gainDb, int ppm)
    {
        return LimeCall.Run(() => OpenOnLimeThread(args, preferredRate, gainDb, ppm));
    }

    private static LimeDevice OpenOnLimeThread(string args, double preferredRate, double? gainDb, int ppm)
    {
        _ = ppm;
        LimeNative.Touch();

        nuint channel = 0;
        string? selectedPort = null;
        string openArgs = args;
        if (args.Contains('|'))
        {
            string[] parts = args.Split('|', StringSplitOptions.TrimEntries);
            openArgs = parts[0];
            for (int p = 1; p < parts.Length; p++)
            {
                if (parts[p].StartsWith("channel=", StringComparison.OrdinalIgnoreCase) && uint.TryParse(parts[p]["channel=".Length..], out uint ch))
                    channel = ch;
                else if (parts[p].StartsWith("port=", StringComparison.OrdinalIgnoreCase))
                    selectedPort = parts[p]["port=".Length..];
            }
        }

        if (LimeNative.LMS_Open(out IntPtr device, openArgs, IntPtr.Zero) != 0 || device == IntPtr.Zero)
            throw new RadioException($"LimeSuite could not open the radio. {LimeNative.LastError()}");

        try
        {
            Check(LimeNative.LMS_Init(device), "init");
            SilenceTransmitter(device);

            int receivers = LimeNative.LMS_GetNumChannels(device, false);
            if (receivers <= (int)channel)
                throw new RadioException($"This LimeSDR has no receive channel {channel + 1}.");

            for (nuint ch = 0; ch < (nuint)receivers; ch++)
                Check(LimeNative.LMS_EnableChannel(device, false, ch, ch == channel), ch == channel ? "enable receive" : "disable channel");

            LimeNative.LMS_SetTestSignal(device, false, channel, 0, 0, 0);

            double rate = ApplyRate(device, LimePlan.ChooseRate(preferredRate));
            List<LimePlan.Port> ports = ReadPorts(device, channel);
            LmsRange frequency = new() { Minimum = 30_000_000, Maximum = 3_800_000_000 };
            try
            {
                if (LimeNative.LMS_GetLOFrequencyRange(device, false, out LmsRange reported) == 0 && reported.Maximum > reported.Minimum)
                {
                    frequency = reported;
                    if (frequency.Minimum < 30_000_000)
                        frequency.Minimum = 30_000_000;
                }
            }
            catch
            {
            }

            double parked = Math.Clamp(100_000_000, frequency.Minimum, frequency.Maximum);
            double filter = ApplyFilter(device, channel, rate);

            int port = -1;
            if (!string.IsNullOrEmpty(selectedPort))
            {
                LimePlan.Port match = ports.FirstOrDefault(p => p.Name.Equals(selectedPort, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(match.Name))
                    port = match.Index;
            }
            if (port < 0)
                port = LimePlan.ReceivePort(ports, parked);

            Check(LimeNative.LMS_SetAntenna(device, false, channel, (nuint)port), "set receive antenna");
            Check(LimeNative.LMS_SetLOFrequency(device, false, channel, parked), "set frequency");
            LimeNative.LMS_Calibrate(device, false, channel, filter, 0);
            ApplyGain(device, channel, gainDb);

            var stream = new LmsStream
            {
                IsTx = 0,
                Channel = (uint)channel,
                FifoSize = 256 * 1024,
                ThroughputVsLatency = 0.5f,
                DataFormat = 0,
                LinkFormat = 0
            };
            if (stream.IsTx != 0)
                throw new RadioException("LimeSDR transmit is disabled.");
            Check(LimeNative.LMS_SetupStream(device, ref stream), "setup receive stream");
            Check(LimeNative.LMS_StartStream(ref stream), "start receive stream");

            string name = SdrCatalog.DisplayName("lime", Describe(args).Name);
            var capabilities = new RadioCapabilities(name, Describe(args).Serial, frequency.Minimum, frequency.Maximum, rate, gainDb, 0, false);
            var radio = new LimeDevice(device, capabilities, ports, stream, filter, channel, selectedPort)
            {
                CenterFrequencyHz = parked,
                _port = port,
                _calibratedHz = parked
            };
            return radio;
        }
        catch
        {
            LimeNative.LMS_Close(device);
            throw;
        }
    }

    public void SetGain(double? gainDb)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        LimeCall.Run(() =>
        {
            ApplyGain(_device, _channel, gainDb);
            return 0;
        });
    }

    public void Tune(double frequencyHz)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        LimeCall.Run(() =>
        {
            TuneOnLimeThread(frequencyHz);
            return 0;
        });
    }

    private void TuneOnLimeThread(double frequencyHz)
    {
        if (frequencyHz < Capabilities.MinFrequencyHz || frequencyHz > Capabilities.MaxFrequencyHz)
            throw new RadioException($"This radio tunes {Capabilities.MinFrequencyHz:0} to {Capabilities.MaxFrequencyHz:0} Hz.");

        if (string.IsNullOrEmpty(_forcedPortName))
        {
            int port = LimePlan.ReceivePort(_ports, frequencyHz);
            if (port != _port)
            {
                Check(LimeNative.LMS_SetAntenna(_device, false, _channel, (nuint)port), "set receive antenna");
                _port = port;
            }
        }
        else if (_port >= 0)
        {
            Check(LimeNative.LMS_SetAntenna(_device, false, _channel, (nuint)_port), "set receive antenna");
        }

        Check(LimeNative.LMS_SetLOFrequency(_device, false, _channel, frequencyHz), "set frequency");
        if (double.IsNaN(_calibratedHz) || Math.Abs(frequencyHz - _calibratedHz) > 5_000_000)
        {
            LimeNative.LMS_Calibrate(_device, false, _channel, _filterHz, 0);
            _calibratedHz = frequencyHz;
        }

        CenterFrequencyHz = frequencyHz;
    }

    public int Read(byte[] buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return LimeCall.Run(() => ReadOnLimeThread(buffer));
    }

    private int ReadOnLimeThread(byte[] buffer)
    {
        int capacity = buffer.Length / ReadAlignment * ReadAlignment;
        if (capacity < ReadAlignment)
            throw new RadioException($"Read at least {ReadAlignment} bytes.");

        int filled = 0;
        int timeouts = 0;
        while (filled < capacity)
        {
            int samples = Math.Min(8192, (capacity - filled) / 8);
            GCHandle pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                int got = LimeNative.LMS_RecvStream(ref _stream, pin.AddrOfPinnedObject() + filled, (nuint)samples, IntPtr.Zero, 200);
                if (got < 0)
                    throw new RadioException($"LimeSuite read failed. {LimeNative.LastError()}");
                if (got == 0)
                {
                    if (++timeouts >= 4)
                        break;
                    continue;
                }

                filled += got * 8;
                timeouts = 0;
            }
            finally
            {
                pin.Free();
            }
        }

        if (filled == 0)
            throw new RadioException("LimeSuite returned no samples.");
        return filled;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        LimeCall.Run(() =>
        {
            DisposeOnLimeThread();
            return 0;
        });
    }

    private void DisposeOnLimeThread()
    {
        if (_streaming)
        {
            LimeNative.LMS_StopStream(ref _stream);
            LimeNative.LMS_DestroyStream(_device, ref _stream);
            _streaming = false;
        }

        SilenceTransmitter(_device);
        LimeNative.LMS_Close(_device);
    }

    private static void SilenceTransmitter(IntPtr device)
    {
        int transmitters = LimeNative.LMS_GetNumChannels(device, true);
        for (nuint channel = 0; channel < (nuint)Math.Max(transmitters, 0); channel++)
        {
            // Path 0 is None. Gain is left untouched so this does not run a transmit calibration.
            if (LimeNative.LMS_SetAntenna(device, true, channel, 0) != 0)
                throw new RadioException($"LimeSuite could not disconnect transmit channel {channel}. {LimeNative.LastError()}");
        }
    }

    private static double ApplyRate(IntPtr device, double preferred)
    {
        double[] attempts = [preferred, LimePlan.VoiceHz, 8_000_000, 10_000_000];
        string? failure = null;
        foreach (double rate in attempts.Distinct())
        {
            if (LimeNative.LMS_SetSampleRate(device, rate, 0) == 0)
            {
                if (LimeNative.LMS_GetSampleRate(device, false, 0, out double host, out _) == 0 && host > 100_000)
                    return host;
                return rate;
            }

            failure = LimeNative.LastError();
        }

        throw new RadioException($"LimeSuite could not set the sample rate. {failure}");
    }

    private static double ApplyFilter(IntPtr device, nuint channel, double sampleRate)
    {
        double bandwidth = sampleRate;
        if (LimeNative.LMS_GetLPFBWRange(device, false, out LmsRange range) == 0 && range.Maximum > range.Minimum)
            bandwidth = Math.Clamp(sampleRate, range.Minimum, range.Maximum);
        Check(LimeNative.LMS_SetLPFBW(device, false, channel, bandwidth), "set receive bandwidth");
        return bandwidth;
    }

    private static void ApplyGain(IntPtr device, nuint channel, double? gainDb)
    {
        uint gain = (uint)Math.Clamp((int)Math.Round(gainDb ?? 40), 0, 73);
        Check(LimeNative.LMS_SetGaindB(device, false, channel, gain), "set receive gain");
    }

    private static List<LimePlan.Port> ReadPorts(IntPtr device, nuint channel = 0)
    {
        int count = LimeNative.LMS_GetAntennaList(device, false, channel, IntPtr.Zero);
        if (count <= 0)
            throw new RadioException($"LimeSuite did not list receive ports. {LimeNative.LastError()}");

        IntPtr names = Marshal.AllocHGlobal(count * 64);
        try
        {
            int written = LimeNative.LMS_GetAntennaList(device, false, channel, names);
            var ports = new List<LimePlan.Port>();
            for (int i = 0; i < written; i++)
            {
                string name = Text(names + i * 64, 0, 64);
                double minimum = 0;
                double maximum = 0;
                try
                {
                    if (LimeNative.LMS_GetAntennaBW(device, false, channel, (nuint)i, out LmsRange range) == 0)
                    {
                        minimum = range.Minimum;
                        maximum = range.Maximum;
                    }
                }
                catch
                {
                    // LimeSuiteNG legacy API can throw SEHException for certain antenna ports
                }

                ports.Add(new LimePlan.Port(i, name, minimum, maximum));
            }

            return ports;
        }
        finally
        {
            Marshal.FreeHGlobal(names);
        }
    }

    private static (string Name, string? Serial) Describe(string args)
    {
        string clean = args.Split('|')[0];
        string name = clean.Split(',')[0].Trim();
        if (string.IsNullOrWhiteSpace(name))
            name = "LimeSDR";
        string? serial = null;
        foreach (string part in clean.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!part.StartsWith("serial=", StringComparison.OrdinalIgnoreCase))
                continue;
            serial = part["serial=".Length..].Trim().TrimStart('0');
            if (serial.Length == 0)
                serial = part["serial=".Length..].Trim();
        }

        return (name, serial);
    }

    public static RadioReport Inspect(RadioDeviceInfo info, double preferredRate) =>
        LimeCall.Run(() => InspectOnThread(info, preferredRate));

    public RadioReport Report(RadioDeviceInfo info, double preferredRate) =>
        LimeCall.Run(() => BuildReport(info, preferredRate, _device, null));

    private static RadioReport InspectOnThread(RadioDeviceInfo info, double preferredRate)
    {
        if (string.IsNullOrWhiteSpace(info.OpenArgs))
            return BuildReport(info, preferredRate, IntPtr.Zero, "This LimeSDR has no open arguments.");

        string openArgs = info.OpenArgs.Split('|')[0];
        LimeNative.Touch();
        if (LimeNative.LMS_Open(out IntPtr device, openArgs, IntPtr.Zero) != 0 || device == IntPtr.Zero)
            return BuildReport(info, preferredRate, IntPtr.Zero, LimeNative.LastError());

        try
        {
            return BuildReport(info, preferredRate, device, null);
        }
        finally
        {
            LimeNative.LMS_Close(device);
        }
    }

    private static RadioReport BuildReport(RadioDeviceInfo info, double preferredRate, IntPtr device, string? connectionNote)
    {
        var facts = new List<RadioFact>();
        RadioText.Identity(facts, info);
        RadioText.Add(facts, "Role", "Receive only");

        if (device != IntPtr.Zero)
            ReadDeviceInfo(facts, device);

        foreach ((string key, string value) in Arguments(info.OpenArgs))
        {
            if (key.Equals("media", StringComparison.OrdinalIgnoreCase))
                RadioText.Add(facts, "USB speed", value);
            else if (key.Equals("addr", StringComparison.OrdinalIgnoreCase))
                RadioText.Add(facts, "USB address", value);
        }

        (string vid, string pid) = VidPid(info.OpenArgs);
        if (vid.Length == 4 && pid.Length == 4)
            UsbDriverFacts.Add(facts, vid, pid, Arguments(info.OpenArgs).FirstOrDefault(pair => pair.Key.Equals("serial", StringComparison.OrdinalIgnoreCase)).Value ?? info.Serial);

        IntPtr version = LimeNative.LMS_GetLibraryVersion();
        RadioText.Add(facts, "LimeSuiteNG", version == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(version) ?? Marshal.PtrToStringAnsi(version));
        UsbDriverFacts.AddLibraryFile(facts, "LimeSuiteNG file", "limesuiteng.dll");

        RadioText.Add(facts, "Sample format", "32-bit float I/Q");
        if (device != IntPtr.Zero)
            ReadChipFacts(facts, device, preferredRate);
        else
            RadioText.Add(facts, "Sample rate", RadioText.Hertz(LimePlan.ChooseRate(preferredRate)) + "/s");

        RadioText.Add(facts, "Note", connectionNote);
        return new RadioReport(info.Name, facts);
    }

    private static void ReadDeviceInfo(List<RadioFact> facts, IntPtr device)
    {
        IntPtr info = LimeNative.LMS_GetDeviceInfo(device);
        if (info == IntPtr.Zero)
            return;

        RadioText.Add(facts, "Device name", Text(info, 0, 32));
        RadioText.Add(facts, "Expansion", Text(info, 32, 32));
        RadioText.Add(facts, "Firmware", Text(info, 64, 16));
        RadioText.Add(facts, "Hardware", Text(info, 80, 16));
        RadioText.Add(facts, "Protocol", Text(info, 96, 16));
        ulong serial = (ulong)Marshal.ReadInt64(info, 112);
        if (serial != 0)
            RadioText.Add(facts, "Board serial", serial.ToString("X16"));
        RadioText.Add(facts, "Gateware", Text(info, 120, 16));
        RadioText.Add(facts, "Gateware target", Text(info, 136, 32));
    }

    private static void ReadChipFacts(List<RadioFact> facts, IntPtr device, double preferredRate)
    {
        int receivers = LimeNative.LMS_GetNumChannels(device, false);
        int transmitters = LimeNative.LMS_GetNumChannels(device, true);
        if (receivers > 0)
            RadioText.Add(facts, "Receive channels", receivers.ToString());
        if (transmitters > 0)
            RadioText.Add(facts, "Transmit channels", $"{transmitters}, unused");

        if (LimeNative.LMS_GetLOFrequencyRange(device, false, out LmsRange frequency) == 0 && frequency.Maximum > frequency.Minimum)
            RadioText.Add(facts, "Tuning range", RadioText.Range(Math.Max(30_000_000, frequency.Minimum), frequency.Maximum));
        RadioText.Add(facts, "Sample rate", RadioText.Hertz(LimePlan.ChooseRate(preferredRate)) + "/s");
        if (LimeNative.LMS_GetLPFBWRange(device, false, out LmsRange filter) == 0 && filter.Maximum > filter.Minimum)
            RadioText.Add(facts, "Analog filter", RadioText.Range(filter.Minimum, filter.Maximum));

        int count = LimeNative.LMS_GetAntennaList(device, false, 0, IntPtr.Zero);
        if (count <= 0)
            return;
        IntPtr names = Marshal.AllocHGlobal(count * 64);
        try
        {
            int written = LimeNative.LMS_GetAntennaList(device, false, 0, names);
            var ports = new List<string>();
            for (int i = 0; i < written; i++)
            {
                string name = Text(names + i * 64, 0, 64);
                if (LimePlan.IsReceivePort(name))
                    ports.Add(name);
            }

            RadioText.Add(facts, "Receive ports", string.Join(", ", ports));
        }
        finally
        {
            Marshal.FreeHGlobal(names);
        }
    }

    private static string Text(IntPtr info, int offset, int size)
    {
        var bytes = new byte[size];
        Marshal.Copy(info + offset, bytes, 0, size);
        int end = Array.IndexOf(bytes, (byte)0);
        if (end < 0)
            end = size;
        if (end == 0)
            return "";
        return System.Text.Encoding.ASCII.GetString(bytes, 0, end).Trim();
    }

    private static List<(string Key, string Value)> Arguments(string? args)
    {
        var pairs = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(args))
            return pairs;
        string clean = args.Split('|')[0];
        foreach (string part in clean.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int split = part.IndexOf('=');
            if (split <= 0)
                continue;
            pairs.Add((part[..split].Trim(), part[(split + 1)..].Trim()));
        }

        return pairs;
    }

    private static (string Vid, string Pid) VidPid(string? args)
    {
        string addr = Arguments(args).FirstOrDefault(pair => pair.Key.Equals("addr", StringComparison.OrdinalIgnoreCase)).Value ?? "";
        string[] parts = addr.Split(':');
        if (parts.Length != 2)
            return ("", "");
        return (parts[0].Trim(), parts[1].Trim());
    }

    private static void Check(int code, string call)
    {
        if (code < 0)
            throw new RadioException($"LimeSuite {call} failed. {LimeNative.LastError()}");
    }
}
