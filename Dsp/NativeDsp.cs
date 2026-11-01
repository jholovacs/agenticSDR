using System.Reflection;
using System.Runtime.InteropServices;

namespace AgenticSdr.Dsp;

/// <summary>Loads optional native DSP helpers (M17/Codec2) from the app folder or Host/native.</summary>
public static class NativeDsp
{
    private static int _installed;

    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0)
            return;
        NativeLibrary.SetDllImportResolver(typeof(NativeDsp).Assembly, Resolve);
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!libraryName.Equals("m17voice", StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero;

        foreach (string path in CandidatePaths())
        {
            if (NativeLibrary.TryLoad(path, out IntPtr handle))
                return handle;
        }

        return IntPtr.Zero;
    }

    private static IEnumerable<string> CandidatePaths()
    {
        string file = OperatingSystem.IsWindows() ? "m17voice.dll" : "libm17voice.so";
        string baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, file);
        yield return Path.Combine(baseDir, "native", file);
        string? cwd = Environment.CurrentDirectory;
        if (!string.IsNullOrWhiteSpace(cwd))
        {
            yield return Path.Combine(cwd, file);
            yield return Path.Combine(cwd, "native", file);
        }
    }
}
