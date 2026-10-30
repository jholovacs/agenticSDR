using System.Reflection;
using System.Runtime.InteropServices;

namespace AgenticSdr.Radio;

/// <summary>
/// The C functions in librtlsdr. Beginners should start at <see cref="RtlSdrDevice"/>; this type only moves bytes to the DLL.
/// </summary>
internal static class RtlSdrNative
{
    private const string Lib = "rtlsdr";

    static RtlSdrNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(RtlSdrNative).Assembly, RtlSdrLibrary.Resolve);
    }

    public static void Touch()
    {
        // Forces the type initializer so the resolver is installed before the first call.
        _ = typeof(RtlSdrNative);
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint rtlsdr_get_device_count();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr rtlsdr_get_device_name(uint index);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtlsdr_get_device_usb_strings(uint index, byte[] manufact, byte[] product, byte[] serial);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtlsdr_open(out IntPtr dev, uint index);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtlsdr_close(IntPtr dev);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtlsdr_set_center_freq(IntPtr dev, uint freq);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtlsdr_set_freq_correction(IntPtr dev, int ppm);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtlsdr_set_tuner_gain_mode(IntPtr dev, int manual);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtlsdr_set_tuner_gain(IntPtr dev, int gain);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtlsdr_set_agc_mode(IntPtr dev, int on);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtlsdr_set_sample_rate(IntPtr dev, uint rate);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtlsdr_set_tuner_bandwidth(IntPtr dev, uint bw);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtlsdr_set_direct_sampling(IntPtr dev, int on);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtlsdr_reset_buffer(IntPtr dev);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtlsdr_read_sync(IntPtr dev, byte[] buf, int len, out int nRead);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rtlsdr_get_tuner_gains(IntPtr dev, IntPtr gains);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rtlsdr_get_tuner_gains")]
    public static extern int rtlsdr_get_tuner_gains_array(IntPtr dev, int[] gains);
}

/// <summary>Finds rtlsdr.dll, libusb, and the matching Linux or macOS library names next to the program or under native/.</summary>
internal static class RtlSdrLibrary
{
    public static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!libraryName.Contains("rtlsdr", StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero;

        foreach (var path in CandidatePaths())
        {
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out var handle))
                return handle;
        }

        string[] bare = OperatingSystem.IsWindows()
            ? ["rtlsdr", "librtlsdr"]
            : OperatingSystem.IsMacOS()
                ? ["librtlsdr.2.dylib", "librtlsdr.dylib", "rtlsdr"]
                : ["librtlsdr.so.0", "librtlsdr.so.2", "librtlsdr.so", "rtlsdr"];

        foreach (var name in bare)
        {
            if (NativeLibrary.TryLoad(name, assembly, searchPath, out var handle))
                return handle;
        }

        return IntPtr.Zero;
    }

    private static IEnumerable<string> CandidatePaths()
    {
        string[] names = OperatingSystem.IsWindows()
            ? ["rtlsdr.dll", "librtlsdr.dll"]
            : OperatingSystem.IsMacOS()
                ? ["librtlsdr.2.dylib", "librtlsdr.dylib"]
                : ["librtlsdr.so.0", "librtlsdr.so.2", "librtlsdr.so"];

        var dirs = new List<string> { AppContext.BaseDirectory };
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            dirs.Add(dir.FullName);
            dirs.Add(Path.Combine(dir.FullName, "native"));
        }

        foreach (var folder in dirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var name in names)
                yield return Path.Combine(folder, name);
        }
    }
}
