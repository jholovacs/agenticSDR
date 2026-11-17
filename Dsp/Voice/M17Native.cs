using System.Runtime.InteropServices;

namespace AgenticSdr.Dsp;

/// <summary>P/Invoke to the bundled m17voice.dll (libm17 frame decode + Codec2 speech).</summary>
internal static class M17Native
{
    private const string Library = "m17voice";

    [DllImport(Library, EntryPoint = "m17_decode_lsf_bytes", CallingConvention = CallingConvention.Cdecl)]
    public static extern uint DecodeLsfBytes(byte[] out30, float[] payloadSymbols);

    [DllImport(Library, EntryPoint = "m17_decode_stream", CallingConvention = CallingConvention.Cdecl)]
    public static extern uint DecodeStream(
        byte[] frameData,
        byte[] lich,
        out ushort frameNumber,
        out byte lichCnt,
        float[] payloadSymbols);

    [DllImport(Library, EntryPoint = "m17_callsign", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void Callsign(byte[] output, byte[] input);

    [DllImport(Library, EntryPoint = "m17voice_create", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr VoiceCreate(int modeBps);

    [DllImport(Library, EntryPoint = "m17voice_destroy", CallingConvention = CallingConvention.Cdecl)]
    public static extern void VoiceDestroy(IntPtr handle);

    [DllImport(Library, EntryPoint = "m17voice_samples_per_frame", CallingConvention = CallingConvention.Cdecl)]
    public static extern int VoiceSamplesPerFrame(IntPtr handle);

    [DllImport(Library, EntryPoint = "m17voice_decode", CallingConvention = CallingConvention.Cdecl)]
    public static extern int VoiceDecode(IntPtr handle, short[] pcmOut, byte[] bits);
}
