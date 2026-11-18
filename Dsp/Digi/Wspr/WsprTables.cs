namespace AgenticSdr.Dsp;

/// <summary>Published WSPR Type-1 constants (K1JT / WSJT-X). Clean-room from the public coding notes.</summary>
internal static class WsprTables
{
    public const int SampleRate = 12_000;
    public const int SamplesPerSymbol = 8_192;
    public const int SymbolCount = 162;
    public const int MessageBits = 50;
    public const int TailBits = 31;
    public const int ConvK = 32;
    public const uint Poly0 = 0xF2D05351u;
    public const uint Poly1 = 0xE4613C47u;
    public const double ToneSpacingHz = SampleRate / (double)SamplesPerSymbol;
    public const double CenterHz = 1_500;
    /// <summary>Occupied 4-FSK span (~6 Hz).</summary>
    public const double BandwidthHz = 6;
    public const int SlotSeconds = 120;
    public const double SignalSeconds = SymbolCount * SamplesPerSymbol / (double)SampleRate;

    /// <summary>Fixed 162-bit sync vector (LSB of each channel symbol).</summary>
    public static readonly byte[] Sync =
    [
        1,1,0,0,0,0,0,0,1,0,0,0,1,1,1,0, 0,0,1,0,0,1,0,1,1,1,1,0,0,0,0,0,
        0,0,1,0,0,1,0,1,0,0,0,0,0,0,1,0, 1,1,0,0,1,1,0,1,0,0,0,1,1,0,1,0,
        0,0,0,1,1,0,1,0,1,0,1,0,1,0,0,1, 0,0,1,0,1,1,0,0,0,1,1,0,1,0,1,0,
        0,0,1,0,0,0,0,0,1,0,0,1,0,0,1,1, 1,0,1,1,0,0,1,1,0,1,0,0,0,1,1,1,
        0,0,0,0,0,1,0,1,0,0,1,1,0,0,0,0, 0,0,0,1,1,0,1,0,1,1,0,0,0,1,1,0,
        0,0
    ];

    public static int BitRev8(int value)
    {
        int x = value & 0xFF;
        x = ((x & 0xF0) >> 4) | ((x & 0x0F) << 4);
        x = ((x & 0xCC) >> 2) | ((x & 0x33) << 2);
        x = ((x & 0xAA) >> 1) | ((x & 0x55) << 1);
        return x;
    }

    public static int Parity32(uint value)
    {
        value ^= value >> 16;
        value ^= value >> 8;
        value ^= value >> 4;
        value &= 0x0Fu;
        return (0x6996 >> (int)value) & 1;
    }
}
