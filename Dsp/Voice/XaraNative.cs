using System.Runtime.InteropServices;
using System.Text;

namespace AgenticSdr.Dsp;

/// <summary>P/Invoke to the bundled <c>xaramonitor</c> DLL (MIT xara VARA HF receive monitor).</summary>
internal static class XaraNative
{
    private const string Library = "xaramonitor";

    static XaraNative() => NativeDsp.Install();

    [DllImport(Library, EntryPoint = "xaramonitor_create", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr Create(uint bandwidthHz, uint sampleRate);

    [DllImport(Library, EntryPoint = "xaramonitor_push_f32", CallingConvention = CallingConvention.Cdecl)]
    public static extern int PushF32(IntPtr handle, float[] samples, nuint count);

    [DllImport(Library, EntryPoint = "xaramonitor_pop", CallingConvention = CallingConvention.Cdecl)]
    public static extern int Pop(IntPtr handle, byte[] buffer, nuint capacity);

    [DllImport(Library, EntryPoint = "xaramonitor_finish", CallingConvention = CallingConvention.Cdecl)]
    public static extern void Finish(IntPtr handle);

    [DllImport(Library, EntryPoint = "xaramonitor_destroy", CallingConvention = CallingConvention.Cdecl)]
    public static extern void Destroy(IntPtr handle);

    [DllImport(Library, EntryPoint = "xaramonitor_version", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr VersionPtr();

    public static bool TryCreate(uint bandwidthHz, uint sampleRate, out IntPtr handle)
    {
        handle = IntPtr.Zero;
        try
        {
            NativeDsp.Install();
            handle = Create(bandwidthHz, sampleRate);
            return handle != IntPtr.Zero;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    public static string? TryPop(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
            return null;
        var buffer = new byte[2048];
        if (Pop(handle, buffer, (nuint)buffer.Length) == 0)
            return null;
        int end = Array.IndexOf(buffer, (byte)0);
        if (end < 0)
            end = buffer.Length;
        if (end == 0)
            return null;
        return Encoding.UTF8.GetString(buffer, 0, end);
    }
}
