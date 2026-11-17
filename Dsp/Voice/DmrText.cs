namespace AgenticSdr.Dsp;

/// <summary>Which DMR traffic to write down. A blank field accepts every value.</summary>
public readonly record struct DmrFilter(int? ColorCode, int? Slot, int? Talkgroup)
{
    public bool Allows(int colorCode, int? slot, int? talkgroup)
    {
        if (ColorCode is int wantedColor && wantedColor != colorCode)
            return false;
        if (Slot is int wantedSlot && slot is int seen && wantedSlot != seen)
            return false;
        if (Talkgroup is int wantedGroup && talkgroup is int seenGroup && wantedGroup != seenGroup)
            return false;
        return true;
    }
}

/// <summary>
/// Reads the published DMR signalling: color code, timeslot, talkgroup, radio ID, talker alias, and short data text.
/// The voice frames use a proprietary codec and are not turned into speech.
/// </summary>
public sealed class DmrReader
{
    private const int SyncSymbols = 24;
    private const int BurstSymbols = 132;

    private static readonly SyncWord[] Syncs =
    [
        new("755FD7DF75F7", false, null),
        new("DFF57D75DF5D", true, null),
        new("7F7D5DD57DFD", false, null),
        new("D5D7F77FD757", true, null),
        new("77D55F7DFD77", false, null),
        new("5D577F7757FF", false, 1),
        new("F7FDD5DDFD55", true, 1),
        new("7DFFD5F55D5F", false, 2),
        new("D7557F5FF7F5", true, 2),
    ];

    private static readonly string[] DataTypes =
    [
        "privacy header",
        "voice LC header",
        "terminator",
        "control block",
        "multi-block header",
        "multi-block continuation",
        "data header",
        "rate 1/2 data",
        "rate 3/4 data",
        "idle",
        "rate 1 data"
    ];

    private readonly AliasAssembly _alias = new();
    private string? _framing;

    public IReadOnlyList<string> Push(ReadOnlySpan<System.Numerics.Complex> iq, double sampleRate, double offsetHz, DmrFilter filter)
    {
        float[] symbols = DmrSync.SymbolStream(iq, sampleRate, offsetHz);
        if (symbols.Length < BurstSymbols)
            return [];

        var lines = new List<string>();
        for (int at = 0; at + SyncSymbols <= symbols.Length; at++)
        {
            int pattern = BestPattern(symbols, at);
            if (pattern < 0)
                continue;
            int burst = at - (BurstSymbols - SyncSymbols) / 2;
            if (burst < 0 || burst + BurstSymbols > symbols.Length)
                continue;
            ReadBurst(symbols.AsSpan(burst, BurstSymbols), Syncs[pattern], filter, lines);
            at = burst + BurstSymbols - 1;
        }

        return lines;
    }

    /// <summary>Builds the 196 on-air bits for a 96-bit information block. Tests use this to build a burst the reader can check.</summary>
    public static int[] Protect(ReadOnlySpan<int> info)
    {
        if (info.Length != 96)
            throw new ArgumentException("A DMR information block is 96 bits.", nameof(info));

        var rows = new int[13, 15];
        int at = 0;
        for (int col = 3; col <= 10; col++)
            rows[0, col] = info[at++];
        for (int row = 1; row <= 8; row++)
        {
            for (int col = 0; col < 11; col++)
                rows[row, col] = info[at++];
        }

        for (int row = 0; row < 9; row++)
        {
            var word = new int[15];
            for (int col = 0; col < 11; col++)
                word[col] = rows[row, col];
            Encode(Hamming15, word, 11, 4);
            for (int col = 11; col < 15; col++)
                rows[row, col] = word[col];
        }

        for (int col = 0; col < 15; col++)
        {
            var word = new int[13];
            for (int row = 0; row < 9; row++)
                word[row] = rows[row, col];
            Encode(Hamming13, word, 9, 4);
            for (int row = 9; row < 13; row++)
                rows[row, col] = word[row];
        }

        var matrix = new int[196];
        int n = 1;
        for (int row = 0; row < 13; row++)
        {
            for (int col = 0; col < 15; col++)
                matrix[n++] = rows[row, col];
        }

        var air = new int[196];
        for (int i = 0; i < 196; i++)
            air[(i * 181) % 196] = matrix[i];
        return air;
    }

    public static int[] SlotType(int colorCode, int dataType)
    {
        int data = ((colorCode & 0xF) << 4) | (dataType & 0xF);
        var word = new int[20];
        for (int i = 0; i < 8; i++)
            word[i] = (data >> (7 - i)) & 1;
        Encode(Golay20, word, 8, 12);
        return word;
    }

    private void ReadBurst(ReadOnlySpan<float> burst, SyncWord sync, DmrFilter filter, List<string> lines)
    {
        float unit = Scale(burst, sync.Levels);
        var dibits = new int[BurstSymbols];
        for (int i = 0; i < BurstSymbols; i++)
            dibits[i] = Nearest(burst[i], unit);

        int[] slotBits = Bits(dibits, 49, 5).Concat(Bits(dibits, 78, 5)).ToArray();
        if (!NearestCodeword(Golay20, slotBits, 8, 12, 3, out int slotData))
            return;

        int color = (slotData >> 4) & 0xF;
        int dataType = slotData & 0xF;
        int? slot = sync.Slot;
        if (!filter.Allows(color, slot, null))
            return;

        string kind = dataType < DataTypes.Length ? DataTypes[dataType] : "data";
        string where = slot is int n ? $", slot {n}" : "";
        string framing = $"DMR color {color}{where}, {kind}";
        if (framing != _framing)
        {
            _framing = framing;
            lines.Add(framing);
        }

        if (!sync.Data)
            return;

        int[] payload = Bits(dibits, 0, 49).Concat(Bits(dibits, 83, 49)).ToArray();
        if (payload.Length != 196 || !TryInfo(payload, out int[] info))
            return;

        if (dataType is 1 or 2)
            DescribeLink(info, color, slot, filter, lines);
        else if (dataType is 7 or 10)
            DescribeBytes(info, lines);
    }

    private void DescribeLink(int[] info, int color, int? slot, DmrFilter filter, List<string> lines)
    {
        int flco = info[0] & 0x3F;
        int dest = Address(info, 3);
        int src = Address(info, 6);
        if (flco is 0x00 or 0x03)
        {
            if (!filter.Allows(color, slot, dest))
                return;
            string where = slot is int n ? $", slot {n}" : "";
            string call = flco == 0x00
                ? $"Talkgroup {dest} from {src}"
                : $"Private call to {dest} from {src}";
            lines.Add($"{call}, color {color}{where}");
            _alias.Source = src;
            return;
        }

        if (flco == 0x04)
            _alias.Header(info);
        else if (flco is >= 0x05 and <= 0x07)
            _alias.Block(info);
        if (_alias.Take(out string alias))
            lines.Add(_alias.Source is int id ? $"Talker alias {id}: {alias}" : $"Talker alias: {alias}");
    }

    private static void DescribeBytes(int[] info, List<string> lines)
    {
        var chars = new List<char>();
        for (int i = 0; i < 12; i++)
        {
            int value = info[i];
            if (value is >= 32 and < 127)
                chars.Add((char)value);
            else if (chars.Count >= 4)
                break;
            else
                chars.Clear();
        }

        if (chars.Count >= 4)
            lines.Add("DMR text: " + new string(chars.ToArray()).Trim());
    }

    private static bool TryInfo(int[] air, out int[] info)
    {
        info = [];
        var matrix = new int[196];
        for (int i = 0; i < 196; i++)
            matrix[i] = air[(i * 181) % 196];

        var rows = new int[13, 15];
        int n = 1;
        for (int row = 0; row < 13; row++)
        {
            for (int col = 0; col < 15; col++)
                rows[row, col] = matrix[n++];
        }

        for (int row = 0; row < 9; row++)
        {
            var word = new int[15];
            for (int col = 0; col < 15; col++)
                word[col] = rows[row, col];
            if (!Correct(Hamming15, word, 11, 4))
                return false;
            for (int col = 0; col < 15; col++)
                rows[row, col] = word[col];
        }

        for (int col = 0; col < 15; col++)
        {
            var word = new int[13];
            for (int row = 0; row < 13; row++)
                word[row] = rows[row, col];
            if (!Correct(Hamming13, word, 9, 4))
                return false;
            for (int row = 0; row < 13; row++)
                rows[row, col] = word[row];
        }

        info = new int[12];
        var bits = new List<int>(96);
        for (int col = 3; col <= 10; col++)
            bits.Add(rows[0, col]);
        for (int row = 1; row <= 8; row++)
        {
            for (int col = 0; col < 11; col++)
                bits.Add(rows[row, col]);
        }

        for (int octet = 0; octet < 12; octet++)
        {
            int value = 0;
            for (int bit = 0; bit < 8; bit++)
                value = (value << 1) | bits[octet * 8 + bit];
            info[octet] = value;
        }

        return true;
    }

    private static int Address(int[] info, int octet) =>
        (info[octet] << 16) | (info[octet + 1] << 8) | info[octet + 2];

    private static int BestPattern(float[] symbols, int at)
    {
        int best = -1;
        double score = 0.72;
        for (int i = 0; i < Syncs.Length; i++)
        {
            if (at + Syncs[i].Levels.Length > symbols.Length)
                continue;
            double now = Correlate(symbols.AsSpan(at, Syncs[i].Levels.Length), Syncs[i].Levels);
            if (now > score)
            {
                score = now;
                best = i;
            }
        }

        return best;
    }

    private static float Scale(ReadOnlySpan<float> burst, int[] levels)
    {
        int sync = (BurstSymbols - levels.Length) / 2;
        double seen = 0;
        double reference = 0;
        for (int i = 0; i < levels.Length; i++)
        {
            seen += Math.Abs(burst[sync + i]);
            reference += Math.Abs(levels[i]);
        }

        return reference < 1 ? 1 : (float)(seen / reference);
    }

    private static int Nearest(float symbol, float unit)
    {
        float x = unit == 0 ? 0 : symbol / unit;
        (float level, int dibit) best = (1, 0b00);
        float distance = float.MaxValue;
        foreach ((float level, int dibit) choice in new[] { (3f, 0b01), (1f, 0b00), (-1f, 0b10), (-3f, 0b11) })
        {
            float gap = Math.Abs(x - choice.level);
            if (gap < distance)
            {
                distance = gap;
                best = choice;
            }
        }

        return best.dibit;
    }

    private static List<int> Bits(int[] dibits, int start, int count)
    {
        var bits = new List<int>(count * 2);
        for (int i = 0; i < count; i++)
        {
            int dibit = dibits[start + i];
            bits.Add((dibit >> 1) & 1);
            bits.Add(dibit & 1);
        }

        return bits;
    }

    private static double Correlate(ReadOnlySpan<float> observed, int[] pattern)
    {
        double mean = 0;
        foreach (float sample in observed)
            mean += sample;
        mean /= observed.Length;
        double dot = 0;
        double signal = 0;
        double reference = 0;
        for (int i = 0; i < pattern.Length; i++)
        {
            double centered = observed[i] - mean;
            dot += centered * pattern[i];
            signal += centered * centered;
            reference += (double)pattern[i] * pattern[i];
        }

        if (signal < 1e-12 || reference < 1e-12)
            return 0;
        return dot / Math.Sqrt(signal * reference);
    }

    private static void Encode(int[][] generator, int[] word, int dataBits, int parityBits)
    {
        for (int p = 0; p < parityBits; p++)
        {
            int bit = 0;
            for (int d = 0; d < dataBits; d++)
                bit ^= word[d] & generator[d][dataBits + p];
            word[dataBits + p] = bit;
        }
    }

    private static bool Correct(int[][] generator, int[] word, int dataBits, int parityBits)
    {
        int syndrome = 0;
        for (int p = 0; p < parityBits; p++)
        {
            int bit = word[dataBits + p];
            for (int d = 0; d < dataBits; d++)
                bit ^= word[d] & generator[d][dataBits + p];
            syndrome = (syndrome << 1) | bit;
        }

        if (syndrome == 0)
            return true;
        for (int d = 0; d < dataBits; d++)
        {
            int column = 0;
            for (int p = 0; p < parityBits; p++)
                column = (column << 1) | generator[d][dataBits + p];
            if (column == syndrome)
            {
                word[d] ^= 1;
                return true;
            }
        }

        for (int p = 0; p < parityBits; p++)
        {
            if (syndrome == 1 << (parityBits - 1 - p))
            {
                word[dataBits + p] ^= 1;
                return true;
            }
        }

        return false;
    }

    private static bool NearestCodeword(int[][] generator, int[] word, int dataBits, int parityBits, int maxDistance, out int data)
    {
        int best = 0;
        int distance = int.MaxValue;
        int limit = 1 << dataBits;
        var trial = new int[dataBits + parityBits];
        for (int value = 0; value < limit; value++)
        {
            for (int i = 0; i < dataBits; i++)
                trial[i] = (value >> (dataBits - 1 - i)) & 1;
            Encode(generator, trial, dataBits, parityBits);
            int gaps = 0;
            for (int i = 0; i < trial.Length; i++)
                gaps += trial[i] ^ word[i];
            if (gaps < distance)
            {
                distance = gaps;
                best = value;
            }
        }

        data = best;
        return distance <= maxDistance;
    }

    private static readonly int[][] Hamming15 = Rows(
        "100000000001001",
        "010000000001101",
        "001000000001111",
        "000100000001110",
        "000010000000111",
        "000001000001010",
        "000000100000101",
        "000000010001011",
        "000000001001100",
        "000000000101110",
        "000000000010011");

    private static readonly int[][] Hamming13 = Rows(
        "1000000001111",
        "0100000001110",
        "0010000000111",
        "0001000001010",
        "0000100000101",
        "0000010001011",
        "0000001001100",
        "0000000100110",
        "0000000010011");

    private static readonly int[][] Golay20 = Rows(
        "10000000001111011010",
        "01000000110110011001",
        "00100000011011001101",
        "00010000001101100111",
        "00001000110111000110",
        "00000100101010010111",
        "00000010100100111110",
        "00000001100011101011");

    private static int[][] Rows(params string[] rows) =>
        rows.Select(row => row.Select(ch => ch == '1' ? 1 : 0).ToArray()).ToArray();

    private sealed class AliasAssembly
    {
        private int _format;
        private int _length;
        private readonly List<byte> _bytes = [];
        public int? Source { get; set; }

        public void Header(int[] info)
        {
            _format = (info[2] >> 6) & 3;
            _length = (info[2] >> 1) & 0x1F;
            _bytes.Clear();
            for (int i = 3; i < 9; i++)
                _bytes.Add((byte)info[i]);
        }

        public void Block(int[] info)
        {
            for (int i = 2; i < 9; i++)
                _bytes.Add((byte)info[i]);
        }

        public bool Take(out string text)
        {
            text = "";
            if (_length <= 0)
                return false;
            text = _format switch
            {
                0 => SevenBit(_bytes, _length),
                3 => Utf16(_bytes, _length),
                _ => EightBit(_bytes, _length)
            };
            if (text.Length < _length)
                return false;
            _length = 0;
            _bytes.Clear();
            return true;
        }

        private static string EightBit(List<byte> bytes, int length)
        {
            if (bytes.Count < length)
                return "";
            var chars = new char[length];
            for (int i = 0; i < length; i++)
                chars[i] = bytes[i] is >= 32 and < 127 ? (char)bytes[i] : ' ';
            return new string(chars).Trim();
        }

        private static string SevenBit(List<byte> bytes, int length)
        {
            var chars = new List<char>();
            int acc = 0;
            int bits = 0;
            foreach (byte value in bytes)
            {
                acc = (acc << 8) | value;
                bits += 8;
                while (bits >= 7 && chars.Count < length)
                {
                    bits -= 7;
                    int ch = (acc >> bits) & 0x7F;
                    chars.Add(ch is >= 32 and < 127 ? (char)ch : ' ');
                }
            }

            return chars.Count < length ? "" : new string(chars.ToArray()).Trim();
        }

        private static string Utf16(List<byte> bytes, int length)
        {
            if (bytes.Count < length * 2)
                return "";
            var chars = new char[length];
            for (int i = 0; i < length; i++)
            {
                int ch = (bytes[i * 2] << 8) | bytes[i * 2 + 1];
                chars[i] = ch is >= 32 and < 127 ? (char)ch : ' ';
            }

            return new string(chars).Trim();
        }
    }

    private readonly record struct SyncWord(string Hex, bool Data, int? Slot)
    {
        public int[] Levels { get; } = LevelsOf(Hex);

        private static int[] LevelsOf(string hex)
        {
            var bits = new List<int>(hex.Length * 4);
            foreach (char c in hex)
            {
                int value = Convert.ToInt32(c.ToString(), 16);
                for (int shift = 3; shift >= 0; shift--)
                    bits.Add((value >> shift) & 1);
            }

            var levels = new int[bits.Count / 2];
            for (int i = 0; i < levels.Length; i++)
            {
                int dibit = (bits[i * 2] << 1) | bits[i * 2 + 1];
                levels[i] = dibit switch
                {
                    0b01 => 3,
                    0b00 => 1,
                    0b10 => -1,
                    _ => -3
                };
            }

            return levels;
        }
    }
}
