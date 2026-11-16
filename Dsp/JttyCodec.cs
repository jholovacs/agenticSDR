namespace AgenticSdr.Dsp;

/// <summary>
/// JTTY channel coding: CRC-12, Gray map, and the rate-½ K=10 tail-biting convolutional code.
/// Conventions match the published WSJT-X 3.2 design notes (clean-room).
/// </summary>
internal static class JttyCodec
{
    /// <summary>Builds the 59-tone frame (sync + Gray-mapped data) for a 32-bit source word.</summary>
    public static void EncodeFrame(uint sourceWord, bool endOfMessage, Span<byte> tones)
    {
        if (tones.Length < JttyTables.FrameSymbols)
            throw new ArgumentException("Need 59 tone slots.", nameof(tones));

        Span<byte> info = stackalloc byte[JttyTables.DataSymbols];
        BuildInfoBits(sourceWord, endOfMessage, info);
        Span<byte> dataTones = stackalloc byte[JttyTables.DataSymbols];
        EncodeTbcc(info, dataTones);

        JttyTables.Sync.CopyTo(tones);
        dataTones.CopyTo(tones[JttyTables.SyncSymbols..]);
    }

    /// <summary>
    /// Soft-decodes 46 data symbols (four tone powers each) into a source word.
    /// Returns false when CRC or the reserved bit fails.
    /// </summary>
    public static bool TryDecodeFrame(ReadOnlySpan<float> tonePowers, out uint sourceWord, out bool endOfMessage)
    {
        sourceWord = 0;
        endOfMessage = false;
        if (tonePowers.Length < JttyTables.DataSymbols * JttyTables.Tones)
            return false;

        Span<float> llr = stackalloc float[JttyTables.DataSymbols * 2];
        for (int i = 0; i < JttyTables.DataSymbols; i++)
        {
            ReadOnlySpan<float> p = tonePowers.Slice(i * JttyTables.Tones, JttyTables.Tones);
            // Gray: 00→0, 01→1, 11→2, 10→3.
            float pB0Zero = p[0] + p[1];
            float pB0One = p[2] + p[3];
            float pB1Zero = p[0] + p[3];
            float pB1One = p[1] + p[2];
            llr[2 * i] = SoftLlr(pB0Zero, pB0One);
            llr[2 * i + 1] = SoftLlr(pB1Zero, pB1One);
        }

        Span<byte> info = stackalloc byte[JttyTables.DataSymbols];
        DecodeTbcc(llr, info);
        if (!CrcMatches(info))
            return false;
        if (info[32] != 0)
            return false;

        uint word = 0;
        for (int i = 0; i < 32; i++)
            word = (word << 1) | info[i];
        if (word == 0)
            return false;

        sourceWord = word;
        endOfMessage = info[33] != 0;
        return true;
    }

    public static void BuildInfoBits(uint sourceWord, bool endOfMessage, Span<byte> info)
    {
        if (info.Length < JttyTables.DataSymbols)
            throw new ArgumentException("Need 46 info bits.", nameof(info));

        for (int i = 0; i < 32; i++)
            info[i] = (byte)((sourceWord >> (31 - i)) & 1);
        info[32] = 0;
        info[33] = endOfMessage ? (byte)1 : (byte)0;
        int crc = CrcRemainder(info[..34]);
        for (int i = 0; i < 12; i++)
            info[34 + i] = (byte)((crc >> (11 - i)) & 1);
    }

    public static bool CrcMatches(ReadOnlySpan<byte> info46) =>
        info46.Length >= JttyTables.DataSymbols && CrcRemainder(info46[..JttyTables.DataSymbols]) == 0;

    /// <summary>MSB-first CRC-12 remainder (poly 0x80F). Over payload+CRC it is zero when valid.</summary>
    public static int CrcRemainder(ReadOnlySpan<byte> bits)
    {
        const int top = 1 << (JttyTables.CrcWidth - 1);
        const int mask = (1 << JttyTables.CrcWidth) - 1;
        int rem = 0;
        foreach (byte bit in bits)
        {
            rem ^= (bit & 1) << (JttyTables.CrcWidth - 1);
            rem = (rem & top) != 0
                ? ((rem << 1) ^ JttyTables.CrcPolynomial) & mask
                : (rem << 1) & mask;
        }

        return rem;
    }

    public static void EncodeTbcc(ReadOnlySpan<byte> info, Span<byte> dataTones)
    {
        int state = 0;
        for (int i = info.Length - 9; i < info.Length; i++)
            state = ((state << 1) | info[i]) & (JttyTables.States - 1);

        for (int i = 0; i < info.Length; i++)
        {
            int reg = ((state << 1) | info[i]) & ((1 << JttyTables.ConstraintLength) - 1);
            byte b0 = Parity(reg & JttyTables.Gen0);
            byte b1 = Parity(reg & JttyTables.Gen1);
            dataTones[i] = (byte)((b0 << 1) | (b0 ^ b1));
            state = reg & (JttyTables.States - 1);
        }
    }

    /// <summary>Tail-biting Viterbi with two wrap-around passes. Soft LLR &gt; 0 means bit 0 preferred.</summary>
    public static void DecodeTbcc(ReadOnlySpan<float> llr, Span<byte> info)
    {
        var metrics = new float[JttyTables.States];
        var metricsNext = new float[JttyTables.States];
        var prevState = new ushort[info.Length * JttyTables.States];
        var prevBit = new byte[info.Length * JttyTables.States];
        metrics.AsSpan().Fill(float.NegativeInfinity);
        metrics[0] = 0;

        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < info.Length; i++)
            {
                metricsNext.AsSpan().Fill(float.NegativeInfinity);
                float y0 = llr[2 * i];
                float y1 = llr[2 * i + 1];
                for (int state = 0; state < JttyTables.States; state++)
                {
                    float m = metrics[state];
                    if (float.IsNegativeInfinity(m))
                        continue;
                    for (int bit = 0; bit < 2; bit++)
                    {
                        int reg = ((state << 1) | bit) & ((1 << JttyTables.ConstraintLength) - 1);
                        int next = reg & (JttyTables.States - 1);
                        byte b0 = Parity(reg & JttyTables.Gen0);
                        byte b1 = Parity(reg & JttyTables.Gen1);
                        float expect0 = b0 == 0 ? 1f : -1f;
                        float expect1 = b1 == 0 ? 1f : -1f;
                        float branch = m + expect0 * y0 + expect1 * y1;
                        if (branch > metricsNext[next])
                        {
                            metricsNext[next] = branch;
                            if (pass == 1)
                            {
                                prevState[i * JttyTables.States + next] = (ushort)state;
                                prevBit[i * JttyTables.States + next] = (byte)bit;
                            }
                        }
                    }
                }

                (metrics, metricsNext) = (metricsNext, metrics);
            }
        }

        int best = 0;
        for (int s = 1; s < JttyTables.States; s++)
        {
            if (metrics[s] > metrics[best])
                best = s;
        }

        int at = best;
        for (int i = info.Length - 1; i >= 0; i--)
        {
            info[i] = prevBit[i * JttyTables.States + at];
            at = prevState[i * JttyTables.States + at];
        }
    }

    private static float SoftLlr(float powerZero, float powerOne)
    {
        double z = Math.Log((powerZero + 1e-12) / (powerOne + 1e-12));
        return (float)Math.Clamp(z, -20, 20);
    }

    private static byte Parity(int value)
    {
        value ^= value >> 16;
        value ^= value >> 8;
        value ^= value >> 4;
        value ^= value >> 2;
        value ^= value >> 1;
        return (byte)(value & 1);
    }
}
