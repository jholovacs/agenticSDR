using System.Reflection;
using System.Runtime.InteropServices;

namespace AgenticSdr.Radio;

/// <summary>
/// Receive calls in LimeSuiteNG. Transmit entry points are not declared here.
/// The library is limesuiteng-legacyapi.dll, with limesuiteng.dll beside it.
/// </summary>
internal static class LimeNative
{
    private const string Lib = "limesuiteng-legacyapi";

    static LimeNative() => NativeLibraries.Install();

    public static void Touch() => _ = typeof(LimeNative);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int LMS_GetDeviceList(IntPtr devices);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int LMS_Open(out IntPtr device, [MarshalAs(UnmanagedType.LPStr)] string? info, IntPtr args);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int LMS_Close(IntPtr device);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int LMS_Init(IntPtr device);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int LMS_GetNumChannels(IntPtr device, [MarshalAs(UnmanagedType.I1)] bool transmit);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int LMS_EnableChannel(IntPtr device, [MarshalAs(UnmanagedType.I1)] bool transmit, nuint channel, [MarshalAs(UnmanagedType.I1)] bool enabled);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int LMS_SetSampleRate(IntPtr device, double rate, nuint oversample);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int LMS_GetSampleRate(IntPtr device, [MarshalAs(UnmanagedType.I1)] bool transmit, nuint channel, out double hostHz, out double radioHz);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int LMS_SetLOFrequency(IntPtr device, [MarshalAs(UnmanagedType.I1)] bool transmit, nuint channel, double frequency);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int LMS_GetLOFrequencyRange(IntPtr device, [MarshalAs(UnmanagedType.I1)] bool transmit, out LmsRange range);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int LMS_GetAntennaList(IntPtr device, [MarshalAs(UnmanagedType.I1)] bool transmit, nuint channel, IntPtr names);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int LMS_SetAntenna(IntPtr device, [MarshalAs(UnmanagedType.I1)] bool transmit, nuint channel, nuint index);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int LMS_GetAntennaBW(IntPtr device, [MarshalAs(UnmanagedType.I1)] bool transmit, nuint channel, nuint index, out LmsRange range);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int LMS_SetGaindB(IntPtr device, [MarshalAs(UnmanagedType.I1)] bool transmit, nuint channel, uint gain);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int LMS_SetLPFBW(IntPtr device, [MarshalAs(UnmanagedType.I1)] bool transmit, nuint channel, double bandwidth);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int LMS_GetLPFBWRange(IntPtr device, [MarshalAs(UnmanagedType.I1)] bool transmit, out LmsRange range);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int LMS_Calibrate(IntPtr device, [MarshalAs(UnmanagedType.I1)] bool transmit, nuint channel, double bandwidth, uint flags);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int LMS_SetTestSignal(IntPtr device, [MarshalAs(UnmanagedType.I1)] bool transmit, nuint channel, int signal, short dcI, short dcQ);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int LMS_SetupStream(IntPtr device, ref LmsStream stream);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int LMS_DestroyStream(IntPtr device, ref LmsStream stream);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int LMS_StartStream(ref LmsStream stream);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int LMS_StopStream(ref LmsStream stream);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int LMS_RecvStream(ref LmsStream stream, IntPtr samples, nuint sampleCount, IntPtr meta, uint timeoutMs);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr LMS_GetDeviceInfo(IntPtr device);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr LMS_GetLibraryVersion();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr LMS_GetLastErrorMessage();

    public static string LastError()
    {
        IntPtr ptr = LMS_GetLastErrorMessage();
        if (ptr == IntPtr.Zero)
            return "unknown LimeSuite error";
        return Marshal.PtrToStringUTF8(ptr) ?? Marshal.PtrToStringAnsi(ptr) ?? "unknown LimeSuite error";
    }

    internal static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!libraryName.Contains("limesuiteng", StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero;

        foreach (string path in CandidatePaths())
        {
            if (!File.Exists(path))
                continue;
            string? folder = Path.GetDirectoryName(path);
            if (folder is not null)
            {
                string core = Path.Combine(folder, "limesuiteng.dll");
                if (File.Exists(core))
                    NativeLibrary.TryLoad(core, out _);
            }

            if (NativeLibrary.TryLoad(path, out IntPtr handle))
                return handle;
        }

        if (NativeLibrary.TryLoad(libraryName, assembly, searchPath, out IntPtr bare))
            return bare;
        return IntPtr.Zero;
    }

    private static IEnumerable<string> CandidatePaths()
    {
        string[] names = OperatingSystem.IsWindows()
            ? ["limesuiteng-legacyapi.dll"]
            : OperatingSystem.IsMacOS()
                ? ["liblimesuiteng-legacyapi.dylib"]
                : ["liblimesuiteng-legacyapi.so"];

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
internal struct LmsRange
{
    public double Minimum;
    public double Maximum;
    public double Step;
}

/// <summary>
/// Matches LimeSuiteNG's lms_stream_t on 64-bit Windows. IsTx stays 0 so the stream is receive.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 32)]
internal struct LmsStream
{
    [FieldOffset(0)] public nuint Handle;
    [FieldOffset(8)] public byte IsTx;
    [FieldOffset(12)] public uint Channel;
    [FieldOffset(16)] public uint FifoSize;
    [FieldOffset(20)] public float ThroughputVsLatency;
    [FieldOffset(24)] public int DataFormat;
    [FieldOffset(28)] public int LinkFormat;
}
