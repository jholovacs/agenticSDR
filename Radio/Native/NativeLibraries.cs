using System.Reflection;
using System.Runtime.InteropServices;

namespace AgenticSdr.Radio;

/// <summary>
/// One native-library hook for the radio assembly. librtlsdr, SoapySDR, and LimeSuiteNG
/// share that assembly, and Windows allows only one hook on it.
/// </summary>
internal static class NativeLibraries
{
    private static int _installed;

    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0)
            return;
        NativeLibrary.SetDllImportResolver(typeof(NativeLibraries).Assembly, Resolve);
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        IntPtr handle = RtlSdrLibrary.Resolve(libraryName, assembly, searchPath);
        if (handle != IntPtr.Zero)
            return handle;
        handle = SoapyNative.Resolve(libraryName, assembly, searchPath);
        if (handle != IntPtr.Zero)
            return handle;
        return LimeNative.Resolve(libraryName, assembly, searchPath);
    }
}
