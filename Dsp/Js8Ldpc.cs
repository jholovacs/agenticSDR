namespace AgenticSdr.Dsp;

/// <summary>
/// The error-correcting code inside JS8. Like FT8 it spreads a short message over 174 channel bits so noise can be repaired,
/// but JS8 carries 87 information bits (72 message + 3 frame flags + a 12-bit checksum) instead of FT8's 91.
/// The checksum rejects a decode that merely looks plausible. Beginners can ignore the matrix and read <see cref="Js8Decoder"/>.
/// </summary>
internal static class Js8Ldpc
{
    /// <summary>Bytes in a 72-bit message.</summary>
    public const int MessageBytes = 9;

    /// <summary>
    /// Builds the 79 tones for one frame: Costas, 29 data tones, Costas, 29 data tones, Costas.
    /// Each data tone is three codeword bits read most-significant first, with no Gray mapping.
    /// </summary>
    public static void EncodeTones(ReadOnlySpan<byte> message, int i3, Span<byte> tones)
    {
        Span<byte> info = stackalloc byte[Js8Tables.LdpcKBytes];
        BuildInfo(message, i3, info);
        Span<byte> codeword = stackalloc byte[Js8Tables.LdpcN];
        Encode174(info, codeword);

        ReadOnlySpan<byte> costas = Js8Tables.Costas;
        int bit = 0;
        for (int symbol = 0; symbol < Js8Tables.Symbols; symbol++)
        {
            if (symbol < 7)
                tones[symbol] = costas[symbol];
            else if (symbol is >= 36 and < 43)
                tones[symbol] = costas[symbol - 36];
            else if (symbol >= 72)
                tones[symbol] = costas[symbol - 72];
            else
            {
                tones[symbol] = (byte)((codeword[bit] << 2) | (codeword[bit + 1] << 1) | codeword[bit + 2]);
                bit += 3;
            }
        }
    }

    /// <summary>Repairs a noisy codeword. On success returns the 72-bit message and the 3 frame flag bits.</summary>
    public static bool TryDecode(ReadOnlySpan<float> logLikelihood, Span<byte> message, out int i3)
    {
        i3 = 0;
        Span<byte> plain = stackalloc byte[Js8Tables.LdpcN];
        Span<float> llr = stackalloc float[Js8Tables.LdpcN];
        logLikelihood.CopyTo(llr);

        if (TryDecodeOnce(llr, plain, message, out i3, iterations: 50))
            return true;

        Span<int> weak = stackalloc int[6];
        FindWeakest(llr, weak);
        for (int n = 1; n <= weak.Length; n++)
        {
            logLikelihood.CopyTo(llr);
            for (int i = 0; i < n; i++)
                llr[weak[i]] = -llr[weak[i]];
            if (TryDecodeOnce(llr, plain, message, out i3, iterations: 40))
                return true;
        }

        return false;
    }

    private static bool TryDecodeOnce(Span<float> logLikelihood, Span<byte> plain, Span<byte> message, out int i3, int iterations)
    {
        i3 = 0;
        if (!BeliefPropagation(logLikelihood, plain, iterations))
            return false;

        // The message sits unscrambled in the last 87 channel positions.
        Span<byte> info = stackalloc byte[Js8Tables.LdpcKBytes];
        info.Clear();
        for (int i = 0; i < Js8Tables.LdpcK; i++)
        {
            if (plain[Js8Tables.LdpcM + i] != 0)
                info[i >> 3] |= (byte)(0x80 >> (i & 7));
        }

        if (!CrcMatches(info))
            return false;

        info[..MessageBytes].CopyTo(message);
        i3 = (info[9] >> 5) & 7;
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

    public static bool CrcMatches(Span<byte> info)
    {
        int extracted = ((info[9] & 0x1F) << 7) | (info[10] >> 1);
        info[9] &= 0xE0;
        info[10] = 0;
        return extracted == Crc(info);
    }

    /// <summary>Lays out the 87 information bits: 72 message, 3 flag bits, 12 checksum bits, one spare zero.</summary>
    public static void BuildInfo(ReadOnlySpan<byte> message, int i3, Span<byte> info)
    {
        info.Clear();
        message[..MessageBytes].CopyTo(info);
        info[9] = (byte)((i3 & 7) << 5);
        int checksum = Crc(info);
        info[9] |= (byte)(checksum >> 7);
        info[10] = (byte)((checksum & 0x7F) << 1);
    }

    /// <summary>12-bit checksum over the 75 payload bits plus one zero bit, then XORed with 42.</summary>
    private static int Crc(ReadOnlySpan<byte> info)
    {
        const int top = 1 << (Js8Tables.CrcWidth - 1);
        const int mask = (1 << Js8Tables.CrcWidth) - 1;
        int remainder = 0;
        int byteIndex = 0;
        for (int bit = 0; bit < Js8Tables.CrcBits; bit++)
        {
            if (bit % 8 == 0)
                remainder ^= info[byteIndex++] << (Js8Tables.CrcWidth - 8);

            remainder = (remainder & top) != 0
                ? ((remainder << 1) ^ Js8Tables.CrcPolynomial) & mask
                : (remainder << 1) & mask;
        }

        return remainder ^ Js8Tables.CrcXor;
    }

    /// <summary>Turns 87 information bits into the 174 channel bits (one per byte).</summary>
    private static void Encode174(ReadOnlySpan<byte> info, Span<byte> codeword)
    {
        ReadOnlySpan<byte> generator = Js8Tables.Generator;
        ReadOnlySpan<byte> order = Js8Tables.ColumnOrder;
        for (int i = 0; i < Js8Tables.LdpcN; i++)
        {
            int bit;
            if (i < Js8Tables.LdpcM)
            {
                int sum = 0;
                int offset = i * Js8Tables.LdpcKBytes;
                for (int j = 0; j < Js8Tables.LdpcKBytes; j++)
                    sum ^= Parity((byte)(info[j] & generator[offset + j]));
                bit = sum & 1;
            }
            else
            {
                int k = i - Js8Tables.LdpcM;
                bit = (info[k >> 3] >> (7 - (k & 7))) & 1;
            }

            codeword[order[i]] = (byte)bit;
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
        var toVariable = new float[Js8Tables.LdpcN, 3];
        var toCheck = new float[Js8Tables.LdpcM, 7];
        ReadOnlySpan<byte> owners = Js8Tables.CheckOwners;
        ReadOnlySpan<byte> checks = Js8Tables.Checks;
        ReadOnlySpan<byte> degree = Js8Tables.CheckDegree;
        int bestErrors = Js8Tables.LdpcM;
        Span<byte> best = stackalloc byte[Js8Tables.LdpcN];

        for (int iter = 0; iter < iterations; iter++)
        {
            int ones = 0;
            for (int n = 0; n < Js8Tables.LdpcN; n++)
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

            for (int m = 0; m < Js8Tables.LdpcM; m++)
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

            for (int n = 0; n < Js8Tables.LdpcN; n++)
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
        ReadOnlySpan<byte> checks = Js8Tables.Checks;
        ReadOnlySpan<byte> degree = Js8Tables.CheckDegree;
        int errors = 0;
        for (int m = 0; m < Js8Tables.LdpcM; m++)
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

    private static double Atanh(double x) => 0.5 * Math.Log((1 + x) / (1 - x));
}
