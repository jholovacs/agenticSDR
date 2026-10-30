using System.Runtime.InteropServices;
using System.Text;

namespace AgenticSdr.Radio;

/// <summary>
/// An open RTL2832U dongle, such as the NESDR SMArt v5, through librtlsdr.
/// Sample rate is fixed at 2.4 MS/s. Below 24 MHz the driver switches itself into direct sampling.
/// </summary>
public sealed class RtlSdrDevice : IRadioDevice
{
    private IntPtr _handle;
    private bool _disposed;

    private RtlSdrDevice(IntPtr handle, RadioCapabilities capabilities)
    {
        _handle = handle;
        Capabilities = capabilities;
        SampleRateHz = capabilities.SampleRateHz;
    }

    public RadioCapabilities Capabilities { get; }
    public bool DirectSampling { get; private set; }
    public double CenterFrequencyHz { get; private set; }
    public double SampleRateHz { get; }
    public SampleEncoding Encoding => SampleEncoding.Cu8;
    public int ReadAlignment => 512;

    public static IReadOnlyList<RadioDeviceInfo> List()
    {
        try
        {
            RtlSdrNative.Touch();
            uint count = RtlSdrNative.rtlsdr_get_device_count();
            var list = new List<RadioDeviceInfo>((int)count);
            for (uint i = 0; i < count; i++)
            {
                string name = Marshal.PtrToStringAnsi(RtlSdrNative.rtlsdr_get_device_name(i)) ?? "RTL-SDR";
                string? manufacturer = null;
                string? product = null;
                string? serial = null;
                var m = new byte[256];
                var p = new byte[256];
                var s = new byte[256];
                if (RtlSdrNative.rtlsdr_get_device_usb_strings(i, m, p, s) == 0)
                {
                    manufacturer = CString(m);
                    product = CString(p);
                    serial = CString(s);
                }

                list.Add(new RadioDeviceInfo((int)i, name, manufacturer, product, serial));
            }

            return list;
        }
        catch (DllNotFoundException ex)
        {
            throw LibraryMissing(ex);
        }
        catch (BadImageFormatException ex)
        {
            throw LibraryMissing(ex);
        }
    }

    public static RtlSdrDevice Open(int index, double sampleRateHz, double? gainDb, int ppm)
    {
        if (Math.Abs(sampleRateHz - RadioLimits.DefaultSampleRateHz) > 1)
        {
            throw new RadioException(
                $"Sample rate must be {RadioLimits.DefaultSampleRateHz:0} Hz so audio can decimate to 48 kHz.");
        }

        try
        {
            RtlSdrNative.Touch();
            RadioDeviceInfo? info = null;
            try
            {
                info = List().FirstOrDefault(d => d.Index == index);
            }
            catch (RadioException)
            {
                info = null;
            }

            int rc = RtlSdrNative.rtlsdr_open(out IntPtr handle, (uint)index);
            if (rc < 0 || handle == IntPtr.Zero)
            {
                throw new RadioException(
                    $"Could not open RTL-SDR device {index} (error {rc}). " +
                    "On Windows, bind the WinUSB driver with Zadig to USB\\VID_0BDA&PID_2838 (NESDR SMArt v5) " +
                    "if the DVB-T driver still owns the dongle.");
            }

            try
            {
                Check(RtlSdrNative.rtlsdr_set_sample_rate(handle, (uint)sampleRateHz), "set_sample_rate");
                ApplyGain(handle, gainDb);
                // Some builds return the applied ppm instead of 0. Only a hard failure matters.
                _ = RtlSdrNative.rtlsdr_set_freq_correction(handle, ppm);
                // Park on VHF before automatic direct sampling. The driver's nested retune
                // otherwise treats a stale 0 Hz as HF and the tuner PLL drops lock.
                Check(RtlSdrNative.rtlsdr_set_center_freq(handle, 100_000_000), "set_center_freq");
                _ = RtlSdrNative.rtlsdr_set_direct_sampling(handle, 0);
                Check(RtlSdrNative.rtlsdr_reset_buffer(handle), "reset_buffer");

                var caps = new RadioCapabilities(
                    info?.Name ?? "RTL-SDR",
                    info?.Serial,
                    RadioLimits.MinFrequencyHz,
                    RadioLimits.MaxFrequencyHz,
                    sampleRateHz,
                    gainDb,
                    ppm,
                    DirectSamplingBelow25MHz: true);
                return new RtlSdrDevice(handle, caps);
            }
            catch
            {
                RtlSdrNative.rtlsdr_close(handle);
                throw;
            }
        }
        catch (DllNotFoundException ex)
        {
            throw LibraryMissing(ex);
        }
        catch (BadImageFormatException ex)
        {
            throw LibraryMissing(ex);
        }
    }

    public void SetGain(double? gainDb)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ApplyGain(_handle, gainDb);
    }

    /// <summary>
    /// Sets the tuner filter width in Hz. Zero asks the driver for its automatic width.
    /// The sample rate stays 2.4 MS/s; this only limits what reaches the converter.
    /// </summary>
    public void SetBandwidth(int bandwidthHz)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (bandwidthHz < 0)
            throw new ArgumentOutOfRangeException(nameof(bandwidthHz));
        Check(RtlSdrNative.rtlsdr_set_tuner_bandwidth(_handle, (uint)bandwidthHz), "set_tuner_bandwidth");
    }

    public void Tune(double frequencyHz)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int mode = RadioLimits.DirectSamplingModeFor(frequencyHz);
        // The driver switches sampling mode itself when center frequency is set,
        // as long as direct-sampling mode stays on automatic (0).
        Check(RtlSdrNative.rtlsdr_set_center_freq(_handle, (uint)Math.Round(frequencyHz)), "set_center_freq");
        CenterFrequencyHz = frequencyHz;
        DirectSampling = mode != 0;
        Check(RtlSdrNative.rtlsdr_reset_buffer(_handle), "reset_buffer");
    }

    public int Read(byte[] buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.Length == 0 || buffer.Length % 512 != 0)
            throw new RadioException("Read length must be a positive multiple of 512 bytes.");

        int rc = RtlSdrNative.rtlsdr_read_sync(_handle, buffer, buffer.Length, out int n);
        if (rc < 0)
            throw new RadioException($"rtlsdr_read_sync failed ({rc}). The dongle may have been unplugged.");
        return n;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_handle != IntPtr.Zero)
        {
            RtlSdrNative.rtlsdr_close(_handle);
            _handle = IntPtr.Zero;
        }
    }

    private static void ApplyGain(IntPtr handle, double? gainDb)
    {
        if (gainDb is null)
        {
            Check(RtlSdrNative.rtlsdr_set_tuner_gain_mode(handle, 0), "set_tuner_gain_mode");
            Check(RtlSdrNative.rtlsdr_set_agc_mode(handle, 1), "set_agc_mode");
            return;
        }

        Check(RtlSdrNative.rtlsdr_set_agc_mode(handle, 0), "set_agc_mode");
        Check(RtlSdrNative.rtlsdr_set_tuner_gain_mode(handle, 1), "set_tuner_gain_mode");
        int requested = (int)Math.Round(gainDb.Value * 10);
        int gain = NearestGain(handle, requested);
        Check(RtlSdrNative.rtlsdr_set_tuner_gain(handle, gain), "set_tuner_gain");
    }

    private static int NearestGain(IntPtr handle, int tenthDb)
    {
        int count = RtlSdrNative.rtlsdr_get_tuner_gains(handle, IntPtr.Zero);
        if (count <= 0)
            return tenthDb;

        var gains = new int[count];
        int read = RtlSdrNative.rtlsdr_get_tuner_gains_array(handle, gains);
        if (read <= 0)
            return tenthDb;

        int best = gains[0];
        int bestDelta = Math.Abs(best - tenthDb);
        for (int i = 1; i < read && i < gains.Length; i++)
        {
            int delta = Math.Abs(gains[i] - tenthDb);
            if (delta < bestDelta)
            {
                best = gains[i];
                bestDelta = delta;
            }
        }

        return best;
    }

    private static void Check(int rc, string operation)
    {
        if (rc < 0)
            throw new RadioException($"rtlsdr_{operation} failed ({rc}).");
    }

    private static string? CString(byte[] bytes)
    {
        int end = Array.IndexOf(bytes, (byte)0);
        if (end < 0)
            end = bytes.Length;
        if (end == 0)
            return null;
        return System.Text.Encoding.ASCII.GetString(bytes, 0, end);
    }

    private static RadioException LibraryMissing(Exception ex)
    {
        return new RadioException(
            "librtlsdr was not found. On Windows run tools/fetch-native.sh and leave rtlsdr.dll in native/. " +
            "On Linux install librtlsdr (package librtlsdr-dev or rtl-sdr). On macOS: brew install librtlsdr.",
            ex);
    }
}
