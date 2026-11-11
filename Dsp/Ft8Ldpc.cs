namespace AgenticSdr.Dsp;

/// <summary>
/// The error-correcting code inside FT8. Extra check bits let a noisy reception be rebuilt into the original message.
/// A checksum rejects a decode that merely looks plausible. Beginners can ignore the matrix and read <see cref="Ft8Decoder"/>.
/// </summary>
internal static class Ft8Ldpc
{
    private const ushort Polynomial = 0x2757;
    private const int CrcWidth = 14;

    public static void EncodeTones(ReadOnlySpan<byte> payload, Span<byte> tones)
    {
        Span<byte> a91 = stackalloc byte[Ft8Tables.LdpcKBytes];
        AddCrc(payload, a91);
        Span<byte> codeword = stackalloc byte[Ft8Tables.LdpcNBytes];
        Encode174(a91, codeword);

        byte mask = 0x80;
        int byteIndex = 0;
        for (int tone = 0; tone < 79; tone++)
        {
            if (tone < 7)
                tones[tone] = Ft8Tables.Costas[tone];
            else if (tone is >= 36 and < 43)
                tones[tone] = Ft8Tables.Costas[tone - 36];
            else if (tone is >= 72 and < 79)
                tones[tone] = Ft8Tables.Costas[tone - 72];
            else
            {
                int bits = 0;
                for (int shift = 2; shift >= 0; shift--)
                {
                    if ((codeword[byteIndex] & mask) != 0)
                        bits |= 1 << shift;
                    mask >>= 1;
                    if (mask == 0)
                    {
                        mask = 0x80;
                        byteIndex++;
                    }
                }

                tones[tone] = Ft8Tables.Gray[bits];
            }
        }
    }

    public static bool TryDecode(ReadOnlySpan<float> logLikelihood, Span<byte> payload)
    {
        Span<byte> plain = stackalloc byte[Ft8Tables.LdpcN];
        Span<float> llr = stackalloc float[Ft8Tables.LdpcN];
        logLikelihood.CopyTo(llr);

        if (TryDecodeOnce(llr, plain, payload, iterations: 50))
            return true;

        // Soft OSD: flip the weakest channel bits and retry BP+CRC.
        Span<int> weak = stackalloc int[6];
        FindWeakest(llr, weak);
        for (int n = 1; n <= weak.Length; n++)
        {
            llr.Clear();
            logLikelihood.CopyTo(llr);
            for (int i = 0; i < n; i++)
                llr[weak[i]] = -llr[weak[i]];
            if (TryDecodeOnce(llr, plain, payload, iterations: 40))
                return true;
        }

        return false;
    }

    private static bool TryDecodeOnce(Span<float> logLikelihood, Span<byte> plain, Span<byte> payload, int iterations)
    {
        if (!BeliefPropagation(logLikelihood, plain, iterations))
            return false;

        Span<byte> a91 = stackalloc byte[Ft8Tables.LdpcKBytes];
        PackBits(plain, Ft8Tables.LdpcK, a91);
        if (!CrcMatches(a91))
            return false;

        a91[..10].CopyTo(payload);
        return true;
    }

    private static void FindWeakest(ReadOnlySpan<float> llr, Span<int> weak)
    {
        Span<float> mag = stackalloc float[weak.Length];
        mag.Fill(float.PositiveInfinity);
        weak.Fill(0);
        for (int i = 0; i < llr.Length; i++)
        {
            float a = Math.Abs(llr[i]);
            for (int s = 0; s < weak.Length; s++)
            {
                if (a >= mag[s])
                    continue;
                for (int k = weak.Length - 1; k > s; k--)
                {
                    mag[k] = mag[k - 1];
                    weak[k] = weak[k - 1];
                }

                mag[s] = a;
                weak[s] = i;
                break;
            }
        }
    }

    public static bool CrcMatches(Span<byte> a91)
    {
        ushort extracted = (ushort)(((a91[9] & 0x07) << 11) | (a91[10] << 3) | (a91[11] >> 5));
        a91[9] &= 0xF8;
        a91[10] = 0;
        return extracted == Crc(a91, 82);
    }

    private static void AddCrc(ReadOnlySpan<byte> payload, Span<byte> a91)
    {
        payload[..10].CopyTo(a91);
        a91[9] &= 0xF8;
        a91[10] = 0;
        ushort checksum = Crc(a91, 82);
        a91[9] |= (byte)(checksum >> 11);
        a91[10] = (byte)(checksum >> 3);
        a91[11] = (byte)(checksum << 5);
    }

    private static ushort Crc(ReadOnlySpan<byte> message, int numBits)
    {
        const int top = 1 << (CrcWidth - 1);
        int remainder = 0;
        int byteIndex = 0;
        for (int bit = 0; bit < numBits; bit++)
        {
            if (bit % 8 == 0)
                remainder ^= message[byteIndex++] << (CrcWidth - 8);

            remainder = (remainder & top) != 0
                ? (remainder << 1) ^ Polynomial
                : remainder << 1;
        }

        return (ushort)(remainder & ((top << 1) - 1));
    }

    private static void Encode174(ReadOnlySpan<byte> message, Span<byte> codeword)
    {
        message[..Ft8Tables.LdpcKBytes].CopyTo(codeword);
        for (int i = Ft8Tables.LdpcKBytes; i < codeword.Length; i++)
            codeword[i] = 0;

        byte mask = (byte)(0x80 >> (Ft8Tables.LdpcK % 8));
        int column = Ft8Tables.LdpcKBytes - 1;
        ReadOnlySpan<byte> generator = Ft8Tables.Generator;
        for (int row = 0; row < Ft8Tables.LdpcM; row++)
        {
            int sum = 0;
            int offset = row * Ft8Tables.LdpcKBytes;
            for (int j = 0; j < Ft8Tables.LdpcKBytes; j++)
                sum ^= Parity((byte)(message[j] & generator[offset + j]));

            if ((sum & 1) != 0)
                codeword[column] |= mask;

            mask >>= 1;
            if (mask == 0)
            {
                mask = 0x80;
                column++;
            }
        }
    }

    private static int Parity(byte value)
    {
        int x = value;
        x ^= x >> 4;
        x ^= x >> 2;
        x ^= x >> 1;
        return x & 1;
    }

    private static bool BeliefPropagation(ReadOnlySpan<float> channel, Span<byte> plain, int iterations)
    {
        var toVariable = new float[Ft8Tables.LdpcN, 3];
        var toCheck = new float[Ft8Tables.LdpcM, 7];
        ReadOnlySpan<byte> owners = Ft8Tables.CheckOwners;
        ReadOnlySpan<byte> checks = Ft8Tables.Checks;
        ReadOnlySpan<byte> degree = Ft8Tables.CheckDegree;
        int bestErrors = Ft8Tables.LdpcM;
        Span<byte> best = stackalloc byte[Ft8Tables.LdpcN];

        for (int iter = 0; iter < iterations; iter++)
        {
            int ones = 0;
            for (int n = 0; n < Ft8Tables.LdpcN; n++)
            {
                float belief = channel[n] + toVariable[n, 0] + toVariable[n, 1] + toVariable[n, 2];
                plain[n] = (byte)(belief > 0 ? 1 : 0);
                ones += plain[n];
            }

            if (ones == 0)
                break;

            int errors = ParityErrors(plain);
            if (errors < bestErrors)
            {
                bestErrors = errors;
                plain.CopyTo(best);
                if (errors == 0)
                    break;
            }

            for (int m = 0; m < Ft8Tables.LdpcM; m++)
            {
                int edges = degree[m];
                for (int edge = 0; edge < edges; edge++)
                {
                    int n = checks[m * 7 + edge] - 1;
                    float belief = channel[n];
                    for (int side = 0; side < 3; side++)
                    {
                        if (owners[n * 3 + side] - 1 != m)
                            belief += toVariable[n, side];
                    }

                    toCheck[m, edge] = (float)Math.Tanh(-belief / 2);
                }
            }

            for (int n = 0; n < Ft8Tables.LdpcN; n++)
            {
                for (int side = 0; side < 3; side++)
                {
                    int m = owners[n * 3 + side] - 1;
                    int edges = degree[m];
                    double product = 1;
                    for (int edge = 0; edge < edges; edge++)
                    {
                        if (checks[m * 7 + edge] - 1 != n)
                            product *= toCheck[m, edge];
                    }

                    product = Math.Clamp(product, -0.999999, 0.999999);
                    toVariable[n, side] = (float)(-2 * Atanh(product));
                }
            }
        }

        if (bestErrors != 0)
            return false;

        best.CopyTo(plain);
        return true;
    }

    private static int ParityErrors(ReadOnlySpan<byte> codeword)
    {
        ReadOnlySpan<byte> checks = Ft8Tables.Checks;
        ReadOnlySpan<byte> degree = Ft8Tables.CheckDegree;
        int errors = 0;
        for (int m = 0; m < Ft8Tables.LdpcM; m++)
        {
            byte sum = 0;
            int edges = degree[m];
            for (int edge = 0; edge < edges; edge++)
                sum ^= codeword[checks[m * 7 + edge] - 1];
            if (sum != 0)
                errors++;
        }

        return errors;
    }

    private static void PackBits(ReadOnlySpan<byte> bits, int count, Span<byte> packed)
    {
        packed.Clear();
        byte mask = 0x80;
        int byteIndex = 0;
        for (int i = 0; i < count; i++)
        {
            if (bits[i] != 0)
                packed[byteIndex] |= mask;
            mask >>= 1;
            if (mask == 0)
            {
                mask = 0x80;
                byteIndex++;
            }
        }
    }

    private static double Atanh(double x) => 0.5 * Math.Log((1 + x) / (1 - x));
}
