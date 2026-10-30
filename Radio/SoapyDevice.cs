using System.Runtime.InteropServices;

namespace AgenticSdr.Radio;

/// <summary>
/// An open radio reached through SoapySDR: Airspy, HackRF, LimeSDR, Pluto, and the other published USB radios.
/// Samples are converted to complex floats. If SoapySDR is not installed, listing these radios returns nothing and the RTL-SDR path still works.
/// </summary>
public sealed class SoapyDevice : IRadioDevice
{
    private readonly IntPtr _device;
    private readonly IntPtr _stream;
    private readonly string _driver;
    private readonly string _format;
    private readonly int _nativeBytes;
    private readonly int _mtu;
    private byte[] _native = [];
    private bool _disposed;

    private SoapyDevice(IntPtr device, IntPtr stream, string driver, string format, string name, string? serial, double minHz, double maxHz, double sampleRate, double? gainDb, int ppm, int mtu)
    {
        _device = device;
        _stream = stream;
        _driver = driver;
        _format = format;
        _nativeBytes = format switch
        {
            "CS16" => 4,
            "CU8" => 2,
            _ => 8
        };
        _mtu = mtu;
        Capabilities = new RadioCapabilities(name, serial, minHz, maxHz, sampleRate, gainDb, ppm, driver.Equals("rtlsdr", StringComparison.OrdinalIgnoreCase));
        SampleRateHz = sampleRate;
    }

    public RadioCapabilities Capabilities { get; }

    public bool DirectSampling { get; private set; }

    public double CenterFrequencyHz { get; private set; }

    public double SampleRateHz { get; }

    public SampleEncoding Encoding => SampleEncoding.Cf32;

    public int ReadAlignment => 8;

    public static IReadOnlyList<RadioDeviceInfo> TryList()
    {
        try
        {
            SoapyNative.Touch();
            IntPtr list = SoapyNative.SoapySDRDevice_enumerateStrArgs("", out nuint length);
            if (list == IntPtr.Zero || length == 0)
                return [];

            var found = new List<RadioDeviceInfo>();
            int stride = Marshal.SizeOf<SoapyKwargs>();
            try
            {
                for (nuint i = 0; i < length; i++)
                {
                    IntPtr entry = list + (int)i * stride;
                    string? driver = SoapyNative.PtrString(SoapyNative.SoapySDRKwargs_get(entry, "driver"));
                    string? label = SoapyNative.PtrString(SoapyNative.SoapySDRKwargs_get(entry, "label"));
                    string? serial = SoapyNative.PtrString(SoapyNative.SoapySDRKwargs_get(entry, "serial"));
                    string? manufacturer = SoapyNative.PtrString(SoapyNative.SoapySDRKwargs_get(entry, "manufacturer"));
                    IntPtr markup = SoapyNative.SoapySDRKwargs_toString(entry);
                    string? args = SoapyNative.PtrString(markup);
                    if (markup != IntPtr.Zero)
                        SoapyNative.SoapySDR_free(markup);

                    if (string.IsNullOrWhiteSpace(driver) || string.IsNullOrWhiteSpace(args))
                        continue;

                    found.Add(new RadioDeviceInfo(
                        found.Count,
                        SdrCatalog.DisplayName(driver, label),
                        manufacturer,
                        label,
                        serial,
                        driver,
                        0,
                        args));
                }
            }
            finally
            {
                SoapyNative.SoapySDRKwargsList_clear(list, length);
            }

            return found;
        }
        catch (DllNotFoundException)
        {
            return [];
        }
        catch (EntryPointNotFoundException)
        {
            return [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    public static SoapyDevice Open(string args, double preferredRate, double? gainDb, int ppm)
    {
        SoapyNative.Touch();
        IntPtr device = SoapyNative.SoapySDRDevice_makeStrArgs(args);
        if (device == IntPtr.Zero)
            throw new RadioException($"SoapySDR could not open '{args}'. {SoapyNative.LastError()}");

        IntPtr stream = IntPtr.Zero;
        try
        {
            string driver = Arg(args, "driver") ?? "soapy";
            List<FrequencySpan> frequency = Ranges(SoapyNative.SoapySDRDevice_getFrequencyRange(device, SoapyNative.Rx, 0, out nuint freqCount), freqCount);
            if (frequency.Count == 0)
                frequency.Add(new FrequencySpan(RadioLimits.MinFrequencyHz, RadioLimits.MaxFrequencyHz));

            double minHz = frequency.Min(span => span.Minimum);
            double maxHz = frequency.Max(span => span.Maximum);
            double rate = ChooseRate(device, preferredRate);
            Check(SoapyNative.SoapySDRDevice_setSampleRate(device, SoapyNative.Rx, 0, rate), "setSampleRate");
            SoapyNative.SoapySDRDevice_setBandwidth(device, SoapyNative.Rx, 0, rate);

            ApplyGain(device, gainDb);
            if (ppm != 0)
            {
                int correction = SoapyNative.SoapySDRDevice_setFrequencyCorrection(device, SoapyNative.Rx, 0, ppm);
                if (correction < 0 && correction != SoapyNative.NotSupported)
                    Check(correction, "setFrequencyCorrection");
            }

            double parked = Park(frequency);
            Check(SoapyNative.SoapySDRDevice_setFrequency(device, SoapyNative.Rx, 0, parked, IntPtr.Zero), "setFrequency");

            (stream, string format) = SetupStream(device);
            nuint mtu = SoapyNative.SoapySDRDevice_getStreamMTU(device, stream);
            int samples = mtu == 0 ? 4096 : (int)Math.Clamp((long)mtu, 256, 65536);
            Check(SoapyNative.SoapySDRDevice_activateStream(device, stream, 0, 0, 0), "activateStream");

            string name = SdrCatalog.DisplayName(driver, Arg(args, "label"));
            return new SoapyDevice(device, stream, driver, format, name, Arg(args, "serial"), minHz, maxHz, rate, gainDb, ppm, samples);
        }
        catch
        {
            if (stream != IntPtr.Zero)
                SoapyNative.SoapySDRDevice_closeStream(device, stream);
            SoapyNative.SoapySDRDevice_unmake(device);
            throw;
        }
    }

    public void SetGain(double? gainDb)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ApplyGain(_device, gainDb);
    }

    public void Tune(double frequencyHz)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (frequencyHz < Capabilities.MinFrequencyHz || frequencyHz > Capabilities.MaxFrequencyHz)
            throw new RadioException($"This radio tunes {Capabilities.MinFrequencyHz:0} to {Capabilities.MaxFrequencyHz:0} Hz.");

        if (_driver.Equals("rtlsdr", StringComparison.OrdinalIgnoreCase))
        {
            string mode = frequencyHz < RadioLimits.DirectSamplingCutoffHz ? "2" : "0";
            SoapyNative.SoapySDRDevice_writeSetting(_device, "direct_samp", mode);
            DirectSampling = mode != "0";
        }

        Check(SoapyNative.SoapySDRDevice_setFrequency(_device, SoapyNative.Rx, 0, frequencyHz, IntPtr.Zero), "setFrequency");
        CenterFrequencyHz = frequencyHz;
    }

    public int Read(byte[] buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int capacity = buffer.Length / ReadAlignment * ReadAlignment;
        if (capacity < ReadAlignment)
            throw new RadioException($"Read at least {ReadAlignment} bytes.");

        int filled = 0;
        int timeouts = 0;
        int samplesOut = capacity / 8;
        while (filled < samplesOut)
        {
            int want = Math.Min(_mtu, samplesOut - filled);
            int nativeBytes = want * _nativeBytes;
            if (_native.Length < nativeBytes)
                _native = new byte[nativeBytes];

            GCHandle data = GCHandle.Alloc(_native, GCHandleType.Pinned);
            var pointers = new IntPtr[] { data.AddrOfPinnedObject() };
            GCHandle table = GCHandle.Alloc(pointers, GCHandleType.Pinned);
            try
            {
                int count = SoapyNative.SoapySDRDevice_readStream(
                    _device,
                    _stream,
                    table.AddrOfPinnedObject(),
                    (nuint)want,
                    out _,
                    out _,
                    new CLong(200_000));

                if (count == SoapyNative.Overflow)
                    continue;
                if (count == SoapyNative.Timeout)
                {
                    if (++timeouts >= 4)
                        break;
                    continue;
                }

                if (count < 0)
                    throw new RadioException($"SoapySDR read failed. {SoapyNative.LastError()}");

                WriteCf32(_native, count, buffer, filled * 8);
                filled += count;
                timeouts = 0;
            }
            finally
            {
                table.Free();
                data.Free();
            }
        }

        if (filled == 0)
            throw new RadioException("SoapySDR returned no samples.");
        return filled * 8;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        SoapyNative.SoapySDRDevice_deactivateStream(_device, _stream, 0, 0);
        SoapyNative.SoapySDRDevice_closeStream(_device, _stream);
        SoapyNative.SoapySDRDevice_unmake(_device);
    }

    private static (IntPtr Stream, string Format) SetupStream(IntPtr device)
    {
        string[] formats = ["CF32", "CS16", "CU8"];
        nuint[] channel = [0];
        foreach (string format in formats)
        {
            IntPtr stream = SoapyNative.SoapySDRDevice_setupStream(device, SoapyNative.Rx, format, channel, 1, IntPtr.Zero);
            if (stream != IntPtr.Zero)
                return (stream, format);
        }

        throw new RadioException($"SoapySDR has no CF32, CS16, or CU8 receive stream. {SoapyNative.LastError()}");
    }

    private void WriteCf32(byte[] native, int samples, byte[] dest, int destOffset)
    {
        if (_format == "CF32")
        {
            Buffer.BlockCopy(native, 0, dest, destOffset, samples * 8);
            return;
        }

        for (int i = 0; i < samples; i++)
        {
            float real;
            float imag;
            if (_format == "CS16")
            {
                real = BitConverter.ToInt16(native, i * 4) / 32768f;
                imag = BitConverter.ToInt16(native, i * 4 + 2) / 32768f;
            }
            else
            {
                real = (native[i * 2] - 127.5f) / 128f;
                imag = (native[i * 2 + 1] - 127.5f) / 128f;
            }

            BitConverter.TryWriteBytes(dest.AsSpan(destOffset + i * 8), real);
            BitConverter.TryWriteBytes(dest.AsSpan(destOffset + i * 8 + 4), imag);
        }
    }

    private static double ChooseRate(IntPtr device, double preferred)
    {
        IntPtr discretePtr = SoapyNative.SoapySDRDevice_listSampleRates(device, SoapyNative.Rx, 0, out nuint discreteCount);
        double[] discrete = ReadDoubles(discretePtr, discreteCount);
        IntPtr rangePtr = SoapyNative.SoapySDRDevice_getSampleRateRange(device, SoapyNative.Rx, 0, out nuint rangeCount);
        var ranges = Ranges(rangePtr, rangeCount);
        double chosen = SdrRates.Choose(discrete, ranges);
        return preferred > 0 && Math.Abs(preferred - chosen) < 1 && Supports(discrete, ranges, preferred)
            ? preferred
            : chosen;
    }

    private static bool Supports(IReadOnlyList<double> discrete, IReadOnlyList<FrequencySpan> ranges, double rate)
    {
        if (discrete.Count > 0)
            return discrete.Any(item => Math.Abs(item - rate) < 1);
        return ranges.Count == 0 || ranges.Any(span => rate >= span.Minimum && rate <= span.Maximum);
    }

    private static void ApplyGain(IntPtr device, double? gainDb)
    {
        if (gainDb is null)
        {
            SoapyNative.SoapySDRDevice_setGainMode(device, SoapyNative.Rx, 0, true);
            return;
        }

        SoapyRange range = SoapyNative.SoapySDRDevice_getGainRange(device, SoapyNative.Rx, 0);
        SoapyNative.SoapySDRDevice_setGainMode(device, SoapyNative.Rx, 0, false);
        double gain = Math.Clamp(gainDb.Value, range.Minimum, Math.Max(range.Minimum, range.Maximum));
        Check(SoapyNative.SoapySDRDevice_setGain(device, SoapyNative.Rx, 0, gain), "setGain");
    }

    private static double Park(IReadOnlyList<FrequencySpan> ranges)
    {
        const double preferred = 100_000_000;
        if (ranges.Any(span => preferred >= span.Minimum && preferred <= span.Maximum))
            return preferred;
        FrequencySpan span = ranges[0];
        return Math.Clamp(preferred, span.Minimum, span.Maximum);
    }

    private static List<FrequencySpan> Ranges(IntPtr pointer, nuint length)
    {
        var spans = new List<FrequencySpan>();
        if (pointer == IntPtr.Zero || length == 0)
            return spans;

        int stride = Marshal.SizeOf<SoapyRange>();
        try
        {
            for (nuint i = 0; i < length; i++)
            {
                SoapyRange range = Marshal.PtrToStructure<SoapyRange>(pointer + (int)i * stride);
                if (range.Maximum > range.Minimum)
                    spans.Add(new FrequencySpan(range.Minimum, range.Maximum));
            }
        }
        finally
        {
            SoapyNative.SoapySDR_free(pointer);
        }

        return spans;
    }

    private static double[] ReadDoubles(IntPtr pointer, nuint length)
    {
        if (pointer == IntPtr.Zero || length == 0)
            return [];

        var values = new double[length];
        try
        {
            Marshal.Copy(pointer, values, 0, (int)length);
        }
        finally
        {
            SoapyNative.SoapySDR_free(pointer);
        }

        return values;
    }

    private static string? Arg(string args, string key)
    {
        foreach (string part in args.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int split = part.IndexOf('=');
            if (split <= 0)
                continue;
            if (part[..split].Equals(key, StringComparison.OrdinalIgnoreCase))
                return part[(split + 1)..];
        }

        return null;
    }

    private static void Check(int code, string call)
    {
        if (code < 0)
            throw new RadioException($"SoapySDR {call} failed ({code}). {SoapyNative.LastError()}");
    }
}
