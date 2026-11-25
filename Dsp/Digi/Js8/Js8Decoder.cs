using System.Numerics;
using MathNet.Numerics;
using MathNet.Numerics.IntegralTransforms;

namespace AgenticSdr.Dsp;

/// <summary>
/// One decoded JS8 frame. AudioHz is where the lowest tone sat in the audio. TimeSeconds is when the signal began in the clip.
/// SnrDb is the estimated strength. Speed is Normal, Fast, Turbo, or Slow.
/// </summary>
public sealed record Js8Message(string Text, double AudioHz, double SnrDb, double TimeSeconds = 0, string Speed = "Normal");

/// <summary>
/// JS8Call receive for Normal, Fast, Turbo, and Slow. Same 79-symbol / LDPC(174,87) frame shape as FT8's cousin:
/// eight FSK tones, three Costas sync blocks, no Gray coding, 87-bit code with a 12-bit checksum.
/// Normal uses Costas set 1 at 6.25 Hz. Fast / Turbo / Slow use Costas set 2 at 10 / 20 / 3.125 Hz.
/// Heartbeat and CQ, directed commands, and Huffman free text print as words.
/// Free-text frames expand JSC dictionary indexes into words from the bundled JS8Call word list.
/// Audio shorter than about 4 seconds returns nothing (Turbo needs a full frame); Slow needs ~26 s.
/// </summary>
public static class Js8Decoder
{
    public const int SampleRate = Js8Tables.SampleRate;
    private const int TimeOsr = 4;
    private const int FreqOsr = 2;
    /// <summary>How many Costas hits to try with LDPC per submode. Cap keeps multi-speed search interactive on noisy clips.</summary>
    private const int MaxCandidates = 40;
    private const float MinSyncScore = 2.0f;
    /// <summary>LDPC is expensive; ignore weak Costas ranks that almost never CRC on the air.</summary>
    private const float MinLdpcScore = 2.4f;
    private const int DecodePasses = 2;

    /// <summary>JS8 submodes: samples per symbol, tone spacing, Costas set, and nominal slot length.</summary>
    private static readonly Submode[] Submodes =
    [
        new("Normal", Js8Tables.SymbolSamples, 6.25f, CostasSet2: false),
        new("Fast", 1_200, 10f, CostasSet2: true),
        new("Turbo", 600, 20f, CostasSet2: true),
        new("Slow", 3_840, 3.125f, CostasSet2: true),
    ];

    /// <summary>Formats decoded JS8 for the page. Each line shows strength, tone pitch, the text, and a plain-language reading.</summary>
    public static string Format(IReadOnlyList<Js8Message> messages)
    {
        if (messages.Count == 0)
            return "";

        var lines = new List<string> { "JS8" };
        foreach (Js8Message message in messages)
        {
            string speed = message.Speed is "Normal" or "" ? "" : $" {message.Speed}";
            string head = $"{message.SnrDb,4:0} dB  {message.AudioHz,6:0} Hz{speed}  {message.Text}";
            string meaning = Explain(message.Text);
            lines.Add(string.IsNullOrEmpty(meaning) ? head : $"{head}\n           {meaning}");
        }

        return string.Join("\n", lines);
    }

    /// <summary>Plain-language reading of a rendered JS8 line for beginners.</summary>
    public static string Explain(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        string t = text.Trim();
        int colon = t.IndexOf(": ", StringComparison.Ordinal);
        if (colon <= 0)
            return "JS8 free text.";

        string from = t[..colon];
        string body = t[(colon + 2)..];
        if (body.StartsWith("@ALLCALL", StringComparison.Ordinal))
            return $"{from} is calling anyone (CQ).";
        if (body.StartsWith("@HB", StringComparison.Ordinal))
            return $"{from} is sending a heartbeat to say it is on the air.";

        string[] parts = body.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return $"{from} is starting a compound-callsign message.";
        string to = parts[0].TrimEnd('>');
        return parts.Length == 2
            ? $"{from} is sending {to} a directed command ({parts[1]})."
            : $"{from} is addressing {to}.";
    }

    /// <summary>
    /// Lists the 79 tones for one frame (tone numbers 0 to 7). Mostly for tests and tools.
    /// </summary>
    public static byte[] Tones(ReadOnlySpan<byte> message, int i3, bool costasSet2 = false)
    {
        var tones = new byte[Js8Tables.Symbols];
        Js8Ldpc.EncodeTones(message, i3, tones, costasSet2);
        return tones;
    }

    /// <summary>
    /// Adds one JS8 frame to the audio. Default is Normal speed. Pass <paramref name="speed"/> as Fast, Turbo, or Slow
    /// for the other Costas-set-2 rates. <paramref name="text"/> is a heartbeat/CQ line, a directed line, or a short
    /// free-text line that fits one frame. A bare "CQ ..." line needs <paramref name="myCall"/> for the sender.
    /// </summary>
    public static void Modulate(
        Span<float> audio,
        int sampleRate,
        string text,
        double audioHz,
        double startSeconds,
        string? myCall = null,
        string speed = "Normal")
    {
        Span<byte> message = stackalloc byte[Js8Ldpc.MessageBytes];
        if (!Js8Text.TryPack(text, myCall, message, out int i3))
            throw new ArgumentException($"Cannot fit JS8 message '{text}' in one frame.", nameof(text));

        Submode mode = ResolveSpeed(speed);
        Span<byte> tones = stackalloc byte[Js8Tables.Symbols];
        Js8Ldpc.EncodeTones(message, i3, tones, mode.CostasSet2);
        Synthesize(audio, sampleRate, tones, audioHz, startSeconds, mode.SymbolSamples);
    }

    /// <summary>
    /// Smooth 8-tone FSK: each tone change is a Gaussian-shaped glide three symbols wide (bandwidth-time 2), with a short fade at both ends.
    /// </summary>
    private static void Synthesize(
        Span<float> audio,
        int sampleRate,
        ReadOnlySpan<byte> tones,
        double audioHz,
        double startSeconds,
        int symbolSamples)
    {
        int samplesPerSymbol = (int)Math.Round(sampleRate * (double)symbolSamples / Js8Tables.SampleRate);
        int symbols = tones.Length;
        double peak = 2 * Math.PI / samplesPerSymbol;
        double shape = Math.PI * Math.Sqrt(2 / Math.Log(2)) * 2.0;

        var pulse = new double[3 * samplesPerSymbol];
        for (int i = 0; i < pulse.Length; i++)
        {
            double t = (i + 1 - 1.5 * samplesPerSymbol) / samplesPerSymbol;
            pulse[i] = 0.5 * (SpecialFunctions.Erf(shape * (t + 0.5)) - SpecialFunctions.Erf(shape * (t - 0.5)));
        }

        var step = new double[(symbols + 2) * samplesPerSymbol];
        for (int j = 0; j < symbols; j++)
        {
            int offset = j * samplesPerSymbol;
            for (int i = 0; i < pulse.Length; i++)
                step[offset + i] += peak * tones[j] * pulse[i];
        }

        // Pretend a neighbor before the first and after the last symbol so the ends do not droop.
        for (int i = 0; i < 2 * samplesPerSymbol; i++)
            step[i] += peak * tones[0] * pulse[samplesPerSymbol + i];
        for (int i = 0; i < samplesPerSymbol; i++)
            step[symbols * samplesPerSymbol + i] += peak * tones[symbols - 1] * pulse[i];

        int length = symbols * samplesPerSymbol;
        int ramp = samplesPerSymbol / 8;
        int start = Math.Max(0, (int)Math.Round(startSeconds * sampleRate));
        double carrier = 2 * Math.PI * audioHz / sampleRate;
        double phase = 0;
        for (int i = 0; i < length; i++)
        {
            double value = Math.Sin(phase);
            if (i < ramp)
                value *= (1 - Math.Cos(Math.PI * i / ramp)) / 2;
            else if (i >= length - ramp)
                value *= (1 - Math.Cos(Math.PI * (length - 1 - i) / ramp)) / 2;

            int index = start + i;
            if (index < audio.Length)
                audio[index] += (float)value;

            phase += carrier + step[samplesPerSymbol + i];
            if (phase > Math.PI)
                phase -= 2 * Math.PI;
        }
    }

    public static IReadOnlyList<Js8Message> Decode(ReadOnlySpan<float> audio, int sampleRate)
    {
        if (audio.Length == 0 || sampleRate < 4_000)
            return [];

        float[] wave = To12K(audio, sampleRate);
        // Turbo is the shortest full frame (~4 s). Anything shorter cannot hold a JS8 transmission.
        if (wave.Length < Js8Tables.Symbols * 600)
            return [];

        var messages = new List<Js8Message>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Take(IEnumerable<Js8Message> hits)
        {
            foreach (Js8Message hit in hits)
            {
                string key = $"{hit.Text}|{hit.AudioHz:0}|{hit.TimeSeconds:0.0}|{hit.Speed}";
                if (!seen.Add(key))
                    continue;
                messages.Add(hit);
            }
        }

        // Normal first (most on-air traffic), with a mirrored-tone retry for HF direct-sampling I/Q flips
        // before the heavier Fast/Turbo/Slow searches.
        if (wave.Length >= Js8Tables.Symbols * Submodes[0].SymbolSamples)
        {
            Take(DecodeSubmode(wave, Submodes[0], mirrorTones: false));
            if (messages.Count == 0)
                Take(DecodeSubmode(wave, Submodes[0], mirrorTones: true));
        }

        if (messages.Count == 0)
        {
            foreach (Submode mode in Submodes)
            {
                if (mode.Name is "Normal" or "Slow")
                    continue;
                if (wave.Length < Js8Tables.Symbols * mode.SymbolSamples)
                    continue;
                Take(DecodeSubmode(wave, mode, mirrorTones: false));
            }
        }

        // Slow needs ~26 s of audio; skip on short live 15 s chops.
        if (messages.Count == 0)
        {
            Submode slow = Submodes[3];
            if (wave.Length >= Js8Tables.Symbols * slow.SymbolSamples)
                Take(DecodeSubmode(wave, slow, mirrorTones: false));
        }

        return messages
            .OrderBy(message => message.TimeSeconds)
            .ThenBy(message => message.AudioHz)
            .ToList();
    }

    private static List<Js8Message> DecodeSubmode(float[] wave, Submode mode, bool mirrorTones)
    {
        List<Js8Message> best = DecodeWave(wave, mode, mirrorTones);
        if (best.Count > 0)
            return best;

        // Pad rescue only for Normal — multi-speed search already walks a coarse DT grid per mode.
        if (mode.Name != "Normal" || mirrorTones)
            return best;

        // One half-hop pad covers sub-hop DT from early slot align; RefineCandidate already covers ±1 symbol.
        int halfHop = Math.Max(1, mode.SymbolSamples / (TimeOsr * 2));
        best = Prefer(best, DecodeWave(PadFront(wave, halfHop), mode, mirrorTones: false));
        if (best.Count > 0)
            return best;
        if (halfHop < wave.Length / 4)
            best = Prefer(best, DecodeWave(SkipFront(wave, halfHop), mode, mirrorTones: false));
        return best;
    }

    private static List<Js8Message> Prefer(List<Js8Message> current, List<Js8Message> next) =>
        next.Count > current.Count ? next : current;

    private static float[] PadFront(float[] wave, int pad)
    {
        var output = new float[wave.Length + pad];
        Array.Copy(wave, 0, output, pad, wave.Length);
        return output;
    }

    private static float[] SkipFront(float[] wave, int skip)
    {
        if (skip <= 0 || skip >= wave.Length)
            return wave;
        var output = new float[wave.Length - skip];
        Array.Copy(wave, skip, output, 0, output.Length);
        return output;
    }

    private static List<Js8Message> DecodeWave(float[] wave, Submode mode, bool mirrorTones)
    {
        Waterfall waterfall = BuildWaterfall(wave, mode);
        var frames = new List<Decoded>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (int pass = 0; pass < DecodePasses; pass++)
        {
            List<Candidate> candidates = FindCandidates(waterfall, mode, mirrorTones);
            int foundThisPass = 0;
            foreach (Candidate rough in candidates)
            {
                Candidate candidate = RefineCandidate(waterfall, rough, mode, mirrorTones);
                if (candidate.Score < MinLdpcScore)
                    continue;
                if (!TryFrame(waterfall, candidate, mode, mirrorTones, out byte[] message, out int i3))
                    continue;

                string key = Convert.ToHexString(message) + i3;
                if (!seen.Add(key))
                    continue;

                frames.Add(new Decoded(message, i3, candidate, mode));
                waterfall.Blank(candidate, mode);
                foundThisPass++;
            }

            if (foundThisPass == 0)
                break;
        }

        var messages = new List<Js8Message>(frames.Count);
        foreach (Decoded item in frames)
            AddMessage(messages, item);

        return messages;
    }

    private static void AddMessage(List<Js8Message> messages, Decoded item)
    {
        string text = Js8Text.Render(item.Message, item.I3);
        if (string.IsNullOrWhiteSpace(text))
            return;

        int hop = item.Mode.SymbolSamples / TimeOsr;
        double hz = (item.Candidate.Bin + item.Candidate.FreqSub / (double)FreqOsr) * item.Mode.SpacingHz;
        double time = item.Candidate.Time * hop / (double)SampleRate;
        var message = new Js8Message(text, hz, item.Candidate.Score, time, item.Mode.Name);
        int index = messages.FindIndex(existing =>
            existing.Text == text
            && existing.Speed == message.Speed
            && Math.Abs(existing.AudioHz - hz) < 50
            && Math.Abs(existing.TimeSeconds - time) < 0.5);
        if (index < 0)
            messages.Add(message);
        else if (message.SnrDb > messages[index].SnrDb)
            messages[index] = message;
    }

    private static bool TryFrame(
        Waterfall waterfall,
        Candidate candidate,
        Submode mode,
        bool mirrorTones,
        out byte[] message,
        out int i3)
    {
        message = new byte[Js8Ldpc.MessageBytes];
        Span<float> llr = stackalloc float[Js8Tables.LdpcN];
        Span<float> powers = stackalloc float[8];
        for (int k = 0; k < Js8Tables.DataSymbols; k++)
        {
            int symbol = k + (k < 29 ? 7 : 14);
            int step = candidate.Time + symbol * TimeOsr;
            for (int j = 0; j < 8; j++)
            {
                int tone = mirrorTones ? 7 - j : j;
                powers[j] = waterfall.At(step, candidate.FreqSub, candidate.Bin + tone);
            }

            // The tone number is the three bits directly, most significant first (no Gray map).
            llr[3 * k] = Max4(powers[4], powers[5], powers[6], powers[7]) - Max4(powers[0], powers[1], powers[2], powers[3]);
            llr[3 * k + 1] = Max4(powers[2], powers[3], powers[6], powers[7]) - Max4(powers[0], powers[1], powers[4], powers[5]);
            llr[3 * k + 2] = Max4(powers[1], powers[3], powers[5], powers[7]) - Max4(powers[0], powers[2], powers[4], powers[6]);
        }

        Normalize(llr);
        return Js8Ldpc.TryDecode(llr, message, out i3);
    }

    private static void Normalize(Span<float> llr)
    {
        double sum = 0;
        double sum2 = 0;
        foreach (float value in llr)
        {
            sum += value;
            sum2 += value * value;
        }

        double inv = 1.0 / llr.Length;
        double variance = (sum2 - sum * sum * inv) * inv;
        if (variance < 1e-6)
            return;
        float scale = (float)Math.Sqrt(24.0 / variance);
        for (int i = 0; i < llr.Length; i++)
            llr[i] *= scale;
    }

    private static List<Candidate> FindCandidates(Waterfall waterfall, Submode mode, bool mirrorTones)
    {
        int last = waterfall.Steps - Js8Tables.Symbols * TimeOsr;
        if (last < 0)
            return [];

        // Starts in the first ~20 s of the clip cover one Slow slot and stacked Normal/Fast periods.
        int hop = mode.SymbolSamples / TimeOsr;
        int maxStart = (int)(20.0 * SampleRate / hop);
        last = Math.Min(last, maxStart);

        // Coarse time grid; RefineCandidate polishes ±TimeOsr afterward. Slow uses a coarser step.
        int timeStep = mode.Name == "Slow" ? TimeOsr : Math.Max(1, TimeOsr / 2);
        var ranked = new List<Candidate>();
        for (int freqSub = 0; freqSub < FreqOsr; freqSub++)
        {
            for (int bin = mode.MinBin; bin <= mode.MaxBin - 8; bin++)
            {
                for (int time = 0; time <= last; time += timeStep)
                {
                    float score = SyncScore(waterfall, time, freqSub, bin, mode, mirrorTones);
                    if (score < MinSyncScore)
                        continue;
                    ranked.Add(new Candidate(time, freqSub, bin, score));
                }
            }
        }

        ranked.Sort(static (a, b) => b.Score.CompareTo(a.Score));
        var kept = new List<Candidate>();
        foreach (Candidate candidate in ranked)
        {
            bool near = kept.Exists(other =>
                Math.Abs(other.Time - candidate.Time) <= TimeOsr
                && Math.Abs(other.Bin - candidate.Bin) <= 1
                && other.FreqSub == candidate.FreqSub);
            if (near)
                continue;
            kept.Add(candidate);
            if (kept.Count == MaxCandidates)
                break;
        }

        return kept;
    }

    /// <summary>
    /// Polish a coarse Costas hit before LDPC — same idea as FT8; on-air JS8 often sits between the coarse grid.
    /// </summary>
    private static Candidate RefineCandidate(Waterfall waterfall, Candidate rough, Submode mode, bool mirrorTones)
    {
        int last = waterfall.Steps - Js8Tables.Symbols * TimeOsr;
        Candidate best = rough;
        for (int dt = -TimeOsr; dt <= TimeOsr; dt++)
        {
            int time = rough.Time + dt;
            if (time < 0 || time > last)
                continue;
            for (int db = -1; db <= 1; db++)
            {
                int bin = rough.Bin + db;
                if (bin < mode.MinBin || bin > mode.MaxBin - 8)
                    continue;
                for (int freqSub = 0; freqSub < FreqOsr; freqSub++)
                {
                    float score = SyncScore(waterfall, time, freqSub, bin, mode, mirrorTones);
                    if (score > best.Score)
                        best = new Candidate(time, freqSub, bin, score);
                }
            }
        }

        return best;
    }

    /// <summary>
    /// Costas sync quality: how far each sync tone sits above the other seven bins in that symbol.
    /// Normal repeats set 1; Fast/Turbo/Slow use the three distinct set-2 arrays.
    /// Blends the full three-group average with the best single group so a clipped slot still ranks.
    /// </summary>
    private static float SyncScore(Waterfall waterfall, int time, int freqSub, int bin, Submode mode, bool mirrorTones)
    {
        float total = 0;
        float bestGroup = float.NegativeInfinity;
        for (int group = 0; group < 3; group++)
        {
            ReadOnlySpan<byte> costas = mode.Costas(group);
            float groupSum = 0;
            for (int k = 0; k < 7; k++)
            {
                int step = time + (group * 36 + k) * TimeOsr;
                int tone = mirrorTones ? 7 - costas[k] : costas[k];
                float peak = waterfall.At(step, freqSub, bin + tone);
                float others = 0;
                for (int j = 0; j < 8; j++)
                {
                    if (j == tone)
                        continue;
                    others += waterfall.At(step, freqSub, bin + j);
                }

                groupSum += peak - others / 7f;
            }

            groupSum /= 7f;
            total += groupSum;
            if (groupSum > bestGroup)
                bestGroup = groupSum;
        }

        float average = total / 3f;
        return Math.Max(average, 0.85f * bestGroup + 0.15f * average);
    }

    private static Waterfall BuildWaterfall(float[] wave, Submode mode)
    {
        int hop = mode.SymbolSamples / TimeOsr;
        int fftSize = mode.SymbolSamples * FreqOsr;
        int numBins = mode.MaxBin - mode.MinBin;
        int steps = Math.Max(0, (wave.Length - mode.SymbolSamples) / hop);
        var magnitude = new float[steps * FreqOsr * numBins];
        var frame = new Complex[fftSize];
        float[] window = Filters.Hann(mode.SymbolSamples);
        for (int step = 0; step < steps; step++)
        {
            Array.Clear(frame);
            int offset = step * hop;
            for (int i = 0; i < mode.SymbolSamples; i++)
                frame[i] = wave[offset + i] * window[i];

            Fourier.Forward(frame, FourierOptions.NoScaling);
            for (int freqSub = 0; freqSub < FreqOsr; freqSub++)
            {
                int dest = (step * FreqOsr + freqSub) * numBins;
                for (int bin = 0; bin < numBins; bin++)
                {
                    int src = (mode.MinBin + bin) * FreqOsr + freqSub;
                    double power = frame[src].Real * frame[src].Real + frame[src].Imaginary * frame[src].Imaginary;
                    magnitude[dest + bin] = (float)(10 * Math.Log10(power + 1e-12));
                }
            }
        }

        return new Waterfall(magnitude, steps, mode.MinBin, numBins);
    }

    private static float[] To12K(ReadOnlySpan<float> audio, int sampleRate)
    {
        if (sampleRate == SampleRate)
            return audio.ToArray();

        if (sampleRate % SampleRate == 0)
        {
            int factor = sampleRate / SampleRate;
            int length = audio.Length / factor;
            var output = new float[length];
            for (int i = 0; i < length; i++)
            {
                float sum = 0;
                int baseIndex = i * factor;
                for (int k = 0; k < factor; k++)
                    sum += audio[baseIndex + k];
                output[i] = sum / factor;
            }

            return output;
        }

        int count = (int)((long)audio.Length * SampleRate / sampleRate);
        var resampled = new float[count];
        double ratio = (double)sampleRate / SampleRate;
        for (int i = 0; i < count; i++)
        {
            double position = i * ratio;
            int left = (int)position;
            int right = Math.Min(left + 1, audio.Length - 1);
            float mix = (float)(position - left);
            resampled[i] = audio[left] * (1 - mix) + audio[right] * mix;
        }

        return resampled;
    }

    private static float Max4(float a, float b, float c, float d) => Math.Max(Math.Max(a, b), Math.Max(c, d));

    private static Submode ResolveSpeed(string speed) => speed.Trim().ToLowerInvariant() switch
    {
        "fast" => Submodes[1],
        "turbo" => Submodes[2],
        "slow" => Submodes[3],
        _ => Submodes[0],
    };

    /// <summary>One JS8 speed: symbol length, tone spacing, and which Costas set to match.</summary>
    private readonly record struct Submode(string Name, int SymbolSamples, float SpacingHz, bool CostasSet2)
    {
        /// <summary>~100 Hz floor in tone bins for this spacing.</summary>
        public int MinBin => Math.Max(1, (int)Math.Floor(100f / SpacingHz));
        /// <summary>~3000 Hz ceiling in tone bins for this spacing.</summary>
        public int MaxBin => Math.Min(SymbolSamples - 1, (int)Math.Ceiling(3_000f / SpacingHz));

        public ReadOnlySpan<byte> Costas(int group) => CostasSet2
            ? group switch
            {
                0 => Js8Tables.CostasSet2Start,
                1 => Js8Tables.CostasSet2Mid,
                _ => Js8Tables.CostasSet2End,
            }
            : Js8Tables.Costas;
    }

    /// <summary>The time-and-frequency picture JS8 is searched on. Each cell is the strength of one tone bin at one moment.</summary>
    private sealed class Waterfall(float[] magnitude, int steps, int minBin, int numBins)
    {
        public int Steps { get; } = steps;

        public float At(int step, int freqSub, int bin)
        {
            int index = bin - minBin;
            if ((uint)step >= (uint)Steps || (uint)freqSub >= FreqOsr || (uint)index >= (uint)numBins)
                return -120;
            return magnitude[(step * FreqOsr + freqSub) * numBins + index];
        }

        /// <summary>Knock down a decoded signal so a later pass can hear weaker neighbors.</summary>
        public void Blank(Candidate candidate, Submode mode)
        {
            for (int symbol = 0; symbol < Js8Tables.Symbols; symbol++)
            {
                int step = candidate.Time + symbol * TimeOsr;
                for (int j = 0; j < 8; j++)
                {
                    int index = candidate.Bin + j - minBin;
                    if ((uint)step >= (uint)Steps || (uint)candidate.FreqSub >= FreqOsr || (uint)index >= (uint)numBins)
                        continue;
                    magnitude[(step * FreqOsr + candidate.FreqSub) * numBins + index] = -120;
                }
            }
        }
    }

    /// <summary>A possible JS8 signal: when it started, which frequency bin it occupies, and how well the sync tones matched.</summary>
    private readonly record struct Candidate(int Time, int FreqSub, int Bin, float Score);

    /// <summary>A candidate whose bits passed the error-correcting checks.</summary>
    private readonly record struct Decoded(byte[] Message, int I3, Candidate Candidate, Submode Mode);
}
