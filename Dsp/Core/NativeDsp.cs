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
        string? file = libraryName.ToLowerInvariant() switch
        {
            "m17voice" => NativeFile("m17voice"),
            "xaramonitor" => NativeFile("xaramonitor"),
            _ => null
        };
        if (file is null)
            return IntPtr.Zero;

        foreach (string path in CandidatePaths(file))
        {
            if (NativeLibrary.TryLoad(path, out IntPtr handle))
                return handle;
        }

        return IntPtr.Zero;
    }

    private static string NativeFile(string stem) =>
        OperatingSystem.IsWindows() ? $"{stem}.dll"
        : OperatingSystem.IsMacOS() ? $"lib{stem}.dylib"
        : $"lib{stem}.so";

    private static IEnumerable<string> CandidatePaths(string file)
    {
        string baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, file);
        yield return Path.Combine(baseDir, "native", file);
        string? cwd = Environment.CurrentDirectory;
        if (!string.IsNullOrWhiteSpace(cwd))
        {
            yield return Path.Combine(cwd, file);
            yield return Path.Combine(cwd, "native", file);
            yield return Path.Combine(cwd, "Host", "native", file);
        }
    }
}
