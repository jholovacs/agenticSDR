using System.Reflection;
using System.Runtime.InteropServices;

namespace AgenticSdr.Radio;

/// <summary>
/// The C functions in SoapySDR. Beginners should start at <see cref="SoapyDevice"/>.
/// These declarations match the library's receive calls and are unused when the DLL is absent.
/// </summary>
internal static class SoapyNative
{
    private const string Lib = "SoapySDR";

    internal const int Rx = 1;
    internal const int Timeout = -1;
    internal const int Overflow = -4;
    internal const int NotSupported = -5;

    static SoapyNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(SoapyNative).Assembly, Resolve);
    }

    public static void Touch() => _ = typeof(SoapyNative);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr SoapySDRDevice_enumerateStrArgs(string args, out nuint length);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr SoapySDRDevice_makeStrArgs(string args);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SoapySDRDevice_unmake(IntPtr device);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr SoapySDRDevice_lastError();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr SoapySDRKwargs_get(IntPtr args, string key);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr SoapySDRKwargs_toString(IntPtr args);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void SoapySDRKwargsList_clear(IntPtr args, nuint length);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void SoapySDR_free(IntPtr ptr);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SoapySDRDevice_setSampleRate(IntPtr device, int direction, nuint channel, double rate);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr SoapySDRDevice_listSampleRates(IntPtr device, int direction, nuint channel, out nuint length);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr SoapySDRDevice_getSampleRateRange(IntPtr device, int direction, nuint channel, out nuint length);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SoapySDRDevice_setFrequency(IntPtr device, int direction, nuint channel, double frequency, IntPtr args);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr SoapySDRDevice_getFrequencyRange(IntPtr device, int direction, nuint channel, out nuint length);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SoapySDRDevice_setBandwidth(IntPtr device, int direction, nuint channel, double bandwidth);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SoapySDRDevice_setGainMode(IntPtr device, int direction, nuint channel, [MarshalAs(UnmanagedType.I1)] bool automatic);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SoapySDRDevice_setGain(IntPtr device, int direction, nuint channel, double value);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern SoapyRange SoapySDRDevice_getGainRange(IntPtr device, int direction, nuint channel);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SoapySDRDevice_setFrequencyCorrection(IntPtr device, int direction, nuint channel, double value);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SoapySDRDevice_writeSetting(IntPtr device, string key, string value);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr SoapySDRDevice_setupStream(IntPtr device, int direction, string format, nuint[]? channels, nuint numChans, IntPtr args);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SoapySDRDevice_closeStream(IntPtr device, IntPtr stream);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern nuint SoapySDRDevice_getStreamMTU(IntPtr device, IntPtr stream);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SoapySDRDevice_activateStream(IntPtr device, IntPtr stream, int flags, long timeNs, nuint numElems);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SoapySDRDevice_deactivateStream(IntPtr device, IntPtr stream, int flags, long timeNs);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SoapySDRDevice_readStream(
        IntPtr device,
        IntPtr stream,
        IntPtr buffs,
        nuint numElems,
        out int flags,
        out long timeNs,
        CLong timeoutUs);

    public static string LastError()
    {
        IntPtr ptr = SoapySDRDevice_lastError();
        return ptr == IntPtr.Zero ? "unknown SoapySDR error" : Marshal.PtrToStringUTF8(ptr) ?? "unknown SoapySDR error";
    }

    public static string? PtrString(IntPtr ptr) =>
        ptr == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(ptr);

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!libraryName.Contains("SoapySDR", StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero;

        foreach (string path in CandidatePaths())
        {
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out IntPtr handle))
                return handle;
        }

        string[] bare = OperatingSystem.IsWindows()
            ? ["SoapySDR"]
            : OperatingSystem.IsMacOS()
                ? ["libSoapySDR.dylib", "libSoapySDR.0.8.dylib", "SoapySDR"]
                : ["libSoapySDR.so.0.8", "libSoapySDR.so", "SoapySDR"];

        foreach (string name in bare)
        {
            if (NativeLibrary.TryLoad(name, assembly, searchPath, out IntPtr handle))
                return handle;
        }

        return IntPtr.Zero;
    }

    private static IEnumerable<string> CandidatePaths()
    {
        string[] names = OperatingSystem.IsWindows()
            ? ["SoapySDR.dll"]
            : OperatingSystem.IsMacOS()
                ? ["libSoapySDR.dylib", "libSoapySDR.0.8.dylib"]
                : ["libSoapySDR.so.0.8", "libSoapySDR.so"];

        var dirs = new List<string> { AppContext.BaseDirectory };
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            dirs.Add(dir.FullName);
            dirs.Add(Path.Combine(dir.FullName, "native"));
        }

        foreach (string folder in dirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (string name in names)
                yield return Path.Combine(folder, name);
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
/// <summary>SoapySDR's list of text keys and values, laid out the way the C library expects.</summary>
internal struct SoapyKwargs
{
    public nuint Size;
    public IntPtr Keys;
    public IntPtr Vals;
}

[StructLayout(LayoutKind.Sequential)]
/// <summary>A minimum, maximum, and step, in the units of the SoapySDR call that returned it.</summary>
internal struct SoapyRange
{
    public double Minimum;
    public double Maximum;
    public double Step;
}
