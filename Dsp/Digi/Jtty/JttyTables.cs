namespace AgenticSdr.Dsp;

/// <summary>
/// JTTY physical-layer constants from the published WSJT-X 3.2 design notes (clean-room; no GPL source).
/// Four-tone GFSK at 31.25 baud; each frame is 13 sync + 46 data symbols (1.888 s).
/// </summary>
internal static class JttyTables
{
    public const int SampleRate = 12_000;
    public const double Baud = 31.25;
    public const int SymbolSamples = 384;
    public const int Tones = 4;
    public const double ToneSpacingHz = 31.25;
    public const double SpanHz = 93.75; // lowest to highest tone; occupied BW ≈ 127 Hz
    public const int SyncSymbols = 13;
    public const int DataSymbols = 46;
    public const int FrameSymbols = SyncSymbols + DataSymbols;
    public const int FrameSamples = FrameSymbols * SymbolSamples;
    public const double FrameSeconds = FrameSymbols / Baud;

    /// <summary>CRC-12 polynomial (x^12 term implied): x^11 + x^3 + x^2 + x + 1.</summary>
    public const int CrcPolynomial = 0x80F;
    public const int CrcWidth = 12;

    /// <summary>TBCC generators, delay-indexed, LSB taps the newest bit (octal 1167 / 1545).</summary>
    public const int Gen0 = 0b1_0011_1011_1; // 1167 octal
    public const int Gen1 = 0b1_1011_0010_1; // 1545 octal
    public const int ConstraintLength = 10;
    public const int States = 1 << (ConstraintLength - 1); // 512

    /// <summary>Costas-style sync tones at the start of every frame.</summary>
    public static ReadOnlySpan<byte> Sync => [0, 2, 2, 3, 0, 0, 3, 2, 1, 3, 1, 2, 0];

    /// <summary>TEXT5 alphabet; index 0 is '0', index 36 is space.</summary>
    public const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ +-./?!\"#$%,&*()_'=[]{}<>|:;";
}
