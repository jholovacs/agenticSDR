using MathNet.Numerics;

namespace AgenticSdr.Dsp;

/// <summary>One JTTY print line with lowest-tone pitch and strength.</summary>
public sealed record JttyMessage(string Text, double AudioHz, double SnrDb, double TimeSeconds = 0);

/// <summary>
/// JTTY receive (WSJT-X 3.2 keyboard/contest mode). Four-tone GFSK at 31.25 baud (~127 Hz wide),
/// asynchronous 1.888 s frames: 13 sync tones, CRC-12, and a rate-½ K=10 tail-biting code.
/// Free text and standard callsign macros print as words; structured contest atoms show as [STRUCT …]
/// until a fuller STRUCT30 table is wired in. Weak or unsynced audio stays quiet.
/// </summary>
public static class JttyDecoder
{
    public const int SampleRate = JttyTables.SampleRate;
    public const double Baud = JttyTables.Baud;
    public const int Tones = JttyTables.Tones;
    public const double SpanHz = 127; // occupied bandwidth (docs); tone span is 93.75 Hz
    private const int SymbolSamples = JttyTables.SymbolSamples;
    private const float MinSyncScore = 1.8f;
    private const int MaxCandidates = 40;

    public static string Format(IReadOnlyList<JttyMessage> messages)
    {
        if (messages.Count == 0)
            return "";
        var lines = new List<string> { "JTTY" };
        foreach (JttyMessage message in messages)
        {
            lines.Add($"{message.SnrDb,4:0} dB  {message.AudioHz,6:0} Hz  {message.Text}");
            lines.Add($"           {Explain(message.Text)}");
        }

        return string.Join("\n", lines);
    }

    public static string Explain(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "JTTY message.";
        string t = text.Trim();
        if (t.StartsWith("CQ ", StringComparison.OrdinalIgnoreCase))
            return "JTTY CQ — calling for a keyboard contact.";
        if (t.StartsWith("TU ", StringComparison.OrdinalIgnoreCase))
            return "Thank you / end of exchange.";
        if (t.Contains("599", StringComparison.Ordinal) || t.StartsWith("[STRUCT", StringComparison.Ordinal))
            return "Contest-style exchange.";
        return "JTTY keyboard text (4-tone GFSK).";
    }

    /// <summary>Synthesizes one or more JTTY frames for <paramref name="text"/> (callsign macro or free text).</summary>
    public static void Modulate(Span<float> audio, int sampleRate, string text, double lowestToneHz = 1_500, float amplitude = 0.45f)
    {
        if (audio.IsEmpty || sampleRate < 4_000 || string.IsNullOrWhiteSpace(text))
            return;

        var words = new List<uint>();
        if (!JttyText.TryPackMessage(text, words))
            throw new ArgumentException($"Cannot pack JTTY message '{text}'.", nameof(text));

        int sps = Math.Max(4, (int)Math.Round(sampleRate * SymbolSamples / (double)SampleRate));
        var allTones = new List<byte>(words.Count * JttyTables.FrameSymbols);
        var frame = new byte[JttyTables.FrameSymbols];
        for (int i = 0; i < words.Count; i++)
        {
            JttyCodec.EncodeFrame(words[i], endOfMessage: i == words.Count - 1, frame);
            allTones.AddRange(frame);
        }

        Synthesize(audio, sampleRate, allTones, lowestToneHz, amplitude, sps);
    }

    /// <summary>Tries one known sync time/frequency (for diagnostics and fixtures).</summary>
    public static bool TryDecodeAt(ReadOnlySpan<float> audio, int sampleRate, double lowestToneHz, double startSeconds, out JttyMessage? message)
    {
        message = null;
        if (audio.Length == 0 || sampleRate < 4_000)
            return false;
        float[] wave = Resample(audio, sampleRate, SampleRate);
        int offset = Math.Max(0, (int)Math.Round(startSeconds * SampleRate));
        if (offset + JttyTables.FrameSamples > wave.Length)
            return false;
        float sync = SyncScore(wave, offset, lowestToneHz);
        if (!TryDemodFrame(wave, offset, lowestToneHz, out uint word, out bool eom, out double snr))
            return false;
        if (!JttyText.TryUnpack(word, out string text) || string.IsNullOrWhiteSpace(text))
            return false;
        _ = sync;
        message = new JttyMessage(eom ? text : text + " (incomplete)", lowestToneHz, snr, startSeconds);
        return true;
    }

    public static IReadOnlyList<JttyMessage> Decode(ReadOnlySpan<float> audio, int sampleRate)
    {
        if (audio.Length < sampleRate || sampleRate < 4_000)
            return [];

        float[] wave = Resample(audio, sampleRate, SampleRate);
        if (wave.Length < JttyTables.FrameSamples)
            return [];

        var frames = new List<(uint Word, bool Eom, double Hz, double Snr, double Time)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // Contest listening band first (1200–1800), then a coarser sweep of the rest of the USB window.
        var centers = new List<double>();
        for (double hz = 1_200; hz <= 1_800; hz += 12.5)
            centers.Add(hz);
        for (double hz = 400; hz <= 2_600; hz += 50)
        {
            if (hz is >= 1_200 and <= 1_800)
                continue;
            centers.Add(hz);
        }

        foreach (double f0 in centers)
        {
            foreach (var hit in FindFrames(wave, f0))
            {
                string key = $"{hit.Word:X8}|{hit.Hz:0}|{hit.Time:0.00}";
                if (!seen.Add(key))
                    continue;
                frames.Add(hit);
            }

            if (frames.Count >= 4)
                break;
        }

        // Sticky re-sweep: once a channel locks, walk frame periods for the rest of the clip.
        foreach (var seed in frames.ToArray())
            FollowChannel(wave, seed.Hz, seed.Time, frames, seen);

        if (frames.Count == 0)
            return [];

        return Assemble(frames);
    }

    private static void FollowChannel(
        float[] wave,
        double hz,
        double seedTime,
        List<(uint Word, bool Eom, double Hz, double Snr, double Time)> frames,
        HashSet<string> seen)
    {
        double frameSec = JttyTables.FrameSeconds;
        int last = wave.Length - JttyTables.FrameSamples;
        // Walk forward and backward from the seed in 1-frame steps, with a small DT polish.
        for (int dir = -1; dir <= 1; dir += 2)
        {
            for (int n = 1; n <= 16; n++)
            {
                double t0 = seedTime + dir * n * frameSec;
                int rough = (int)Math.Round(t0 * SampleRate);
                if (rough < 0 || rough > last)
                    break;

                bool got = false;
                for (int d = -SymbolSamples / 2; d <= SymbolSamples / 2; d += SymbolSamples / 8)
                {
                    int off = rough + d;
                    if (off < 0 || off > last)
                        continue;
                    foreach (double df in new[] { -JttyTables.ToneSpacingHz / 4, 0.0, JttyTables.ToneSpacingHz / 4 })
                    {
                        if (!TryDemodFrame(wave, off, hz + df, out uint word, out bool eom, out double snr))
                            continue;
                        string key = $"{word:X8}|{hz + df:0}|{off / (double)SampleRate:0.00}";
                        if (!seen.Add(key))
                        {
                            got = true;
                            break;
                        }

                        frames.Add((word, eom, hz + df, snr, off / (double)SampleRate));
                        got = true;
                        break;
                    }

                    if (got)
                        break;
                }

                if (!got)
                    break; // gap — stop walking this direction
            }
        }
    }

    private static List<(uint Word, bool Eom, double Hz, double Snr, double Time)> FindFrames(float[] wave, double f0)
    {
        var found = new List<(uint Word, bool Eom, double Hz, double Snr, double Time)>();
        int hop = SymbolSamples / 2; // 16 ms coarse grid; refine winners ±hop/2
        int last = wave.Length - JttyTables.FrameSamples;
        if (last < 0)
            return found;

        var ranked = new List<(int Offset, float Score)>();
        for (int offset = 0; offset <= last; offset += hop)
        {
            float score = SyncScore(wave, offset, f0);
            if (score < MinSyncScore)
                continue;
            ranked.Add((offset, score));
        }

        ranked.Sort(static (a, b) => b.Score.CompareTo(a.Score));
        int kept = 0;
        foreach ((int rough, float _) in ranked)
        {
            if (kept >= MaxCandidates)
                break;

            int bestOff = rough;
            float bestScore = SyncScore(wave, rough, f0);
            for (int d = -hop / 2; d <= hop / 2; d += Math.Max(1, hop / 8))
            {
                int off = rough + d;
                if (off < 0 || off > last)
                    continue;
                float s = SyncScore(wave, off, f0);
                if (s > bestScore)
                {
                    bestScore = s;
                    bestOff = off;
                }
            }

            // Frequency polish: half- and quarter-tone steps around the coarse center.
            double bestHz = f0;
            foreach (double df in new[]
                     {
                         -JttyTables.ToneSpacingHz, -JttyTables.ToneSpacingHz / 2, -JttyTables.ToneSpacingHz / 4,
                         0.0,
                         JttyTables.ToneSpacingHz / 4, JttyTables.ToneSpacingHz / 2, JttyTables.ToneSpacingHz
                     })
            {
                float s = SyncScore(wave, bestOff, f0 + df);
                if (s > bestScore)
                {
                    bestScore = s;
                    bestHz = f0 + df;
                }
            }

            if (found.Exists(f => Math.Abs(f.Time * SampleRate - bestOff) < SymbolSamples && Math.Abs(f.Hz - bestHz) < 10))
                continue;

            if (!TryDemodFrame(wave, bestOff, bestHz, out uint word, out bool eom, out double snr))
                continue;

            found.Add((word, eom, bestHz, snr, bestOff / (double)SampleRate));
            kept++;
        }

        return found;
    }

    private static float SyncScore(float[] wave, int offset, double f0)
    {
        ReadOnlySpan<byte> sync = JttyTables.Sync;
        Span<float> powers = stackalloc float[Tones];
        float total = 0;
        for (int i = 0; i < sync.Length; i++)
        {
            int start = offset + i * SymbolSamples;
            TonePowers(wave.AsSpan(start, SymbolSamples), f0, powers);
            float peak = powers[sync[i]];
            float others = 0;
            for (int t = 0; t < Tones; t++)
            {
                if (t == sync[i])
                    continue;
                others += powers[t];
            }

            total += peak - others / 3f;
        }

        return total / sync.Length;
    }

    private static bool TryDemodFrame(
        float[] wave,
        int offset,
        double f0,
        out uint word,
        out bool eom,
        out double snrDb)
    {
        word = 0;
        eom = false;
        snrDb = -40;
        Span<float> tonePowers = stackalloc float[JttyTables.DataSymbols * Tones];
        double peakSum = 0;
        double otherSum = 0;
        for (int i = 0; i < JttyTables.DataSymbols; i++)
        {
            int start = offset + (JttyTables.SyncSymbols + i) * SymbolSamples;
            Span<float> powers = tonePowers.Slice(i * Tones, Tones);
            TonePowers(wave.AsSpan(start, SymbolSamples), f0, powers);
            float best = powers[0];
            float sum = powers[0];
            for (int t = 1; t < Tones; t++)
            {
                sum += powers[t];
                if (powers[t] > best)
                    best = powers[t];
            }

            peakSum += best;
            otherSum += Math.Max(1e-18, (sum - best) / 3.0);
        }

        snrDb = Math.Clamp(10 * Math.Log10(Math.Max(peakSum, 1e-18) / Math.Max(otherSum, 1e-18)), -20, 40);
        return JttyCodec.TryDecodeFrame(tonePowers, out word, out eom);
    }

    private static void TonePowers(ReadOnlySpan<float> samples, double f0, Span<float> powers)
    {
        for (int t = 0; t < Tones; t++)
        {
            double freq = f0 + t * JttyTables.ToneSpacingHz;
            powers[t] = (float)Goertzel(samples, SampleRate, freq);
        }
    }

    private static List<JttyMessage> Assemble(List<(uint Word, bool Eom, double Hz, double Snr, double Time)> frames)
    {
        // Keep the strongest frame in each ~half-frame / 15 Hz bucket.
        var unique = frames
            .GroupBy(f => (Slot: (int)Math.Round(f.Time / (JttyTables.FrameSeconds * 0.5)), Bin: (int)Math.Round(f.Hz / 15.0)))
            .Select(g => g.OrderByDescending(f => f.Snr).First())
            .OrderBy(f => f.Time)
            .ThenBy(f => f.Hz)
            .ToList();

        var messages = new List<JttyMessage>();
        var used = new bool[unique.Count];

        for (int i = 0; i < unique.Count; i++)
        {
            if (used[i])
                continue;

            var chain = new List<(uint Word, bool Eom)> { (unique[i].Word, unique[i].Eom) };
            used[i] = true;
            double hz = unique[i].Hz;
            double t0 = unique[i].Time;
            double snr = unique[i].Snr;
            int cursor = i;

            while (chain.Count < 16 && !chain[^1].Eom)
            {
                double expect = t0 + chain.Count * JttyTables.FrameSeconds;
                int best = -1;
                double bestDt = double.MaxValue;
                for (int j = cursor + 1; j < unique.Count; j++)
                {
                    if (used[j])
                        continue;
                    if (Math.Abs(unique[j].Hz - hz) > 25)
                        continue;
                    double dt = Math.Abs(unique[j].Time - expect);
                    if (dt > 0.25)
                        continue;
                    if (dt < bestDt)
                    {
                        bestDt = dt;
                        best = j;
                    }
                }

                if (best < 0)
                    break;
                used[best] = true;
                chain.Add((unique[best].Word, unique[best].Eom));
                snr = Math.Max(snr, unique[best].Snr);
                cursor = best;
            }

            string text = JttyText.RenderMessage(chain);
            if (string.IsNullOrWhiteSpace(text))
                continue;
            if (!chain[^1].Eom)
                text += " (incomplete)";
            messages.Add(new JttyMessage(text, hz, snr, t0));
        }

        // Prefer complete, longer lines; drop fragments contained in a longer message.
        messages = messages
            .OrderByDescending(m => m.Text.Contains("(incomplete)", StringComparison.Ordinal) ? 0 : 1)
            .ThenByDescending(m => m.Text.Length)
            .ThenByDescending(m => m.SnrDb)
            .ToList();
        var kept = new List<JttyMessage>();
        foreach (JttyMessage message in messages)
        {
            string core = message.Text.Replace(" (incomplete)", "", StringComparison.Ordinal);
            if (kept.Any(k => k.Text.Replace(" (incomplete)", "", StringComparison.Ordinal).Contains(core, StringComparison.Ordinal)))
                continue;
            kept.Add(message);
            if (kept.Count == 6)
                break;
        }

        return kept
            .OrderBy(m => m.TimeSeconds)
            .ThenBy(m => m.AudioHz)
            .ToList();
    }

    private static void Synthesize(
        Span<float> audio,
        int sampleRate,
        List<byte> tones,
        double lowestToneHz,
        float amplitude,
        int sps)
    {
        double peak = 2 * Math.PI / sps;
        double shape = Math.PI * Math.Sqrt(2 / Math.Log(2)) * 2.0;
        var pulse = new double[3 * sps];
        for (int i = 0; i < pulse.Length; i++)
        {
            double t = (i + 1 - 1.5 * sps) / sps;
            pulse[i] = 0.5 * (SpecialFunctions.Erf(shape * (t + 0.5)) - SpecialFunctions.Erf(shape * (t - 0.5)));
        }

        var step = new double[(tones.Count + 2) * sps];
        for (int j = 0; j < tones.Count; j++)
        {
            int offset = j * sps;
            for (int i = 0; i < pulse.Length; i++)
                step[offset + i] += peak * tones[j] * pulse[i];
        }

        for (int i = 0; i < 2 * sps; i++)
            step[i] += peak * tones[0] * pulse[sps + i];
        for (int i = 0; i < sps; i++)
            step[tones.Count * sps + i] += peak * tones[^1] * pulse[i];

        int length = tones.Count * sps;
        int ramp = Math.Min(48, sps / 2);
        double carrier = 2 * Math.PI * lowestToneHz / sampleRate;
        double phase = 0;
        for (int i = 0; i < length && i < audio.Length; i++)
        {
            double value = Math.Sin(phase) * amplitude;
            if (i < ramp)
                value *= (1 - Math.Cos(Math.PI * i / ramp)) / 2;
            else if (i >= length - ramp)
                value *= (1 - Math.Cos(Math.PI * (length - 1 - i) / ramp)) / 2;
            audio[i] += (float)value;
            phase += carrier + step[sps + i];
            if (phase > Math.PI)
                phase -= 2 * Math.PI;
        }
    }

    private static float[] Resample(ReadOnlySpan<float> audio, int fromRate, int toRate)
    {
        if (fromRate == toRate)
            return audio.ToArray();
        double step = fromRate / (double)toRate;
        int count = Math.Max(1, (int)(audio.Length / step));
        var output = new float[count];
        for (int i = 0; i < count; i++)
        {
            double at = i * step;
            int lo = (int)at;
            int hi = Math.Min(audio.Length - 1, lo + 1);
            double frac = at - lo;
            output[i] = (float)(audio[lo] * (1 - frac) + audio[hi] * frac);
        }

        return output;
    }

    private static double Goertzel(ReadOnlySpan<float> samples, int sampleRate, double frequencyHz)
    {
        double w = 2 * Math.PI * frequencyHz / sampleRate;
        double coeff = 2 * Math.Cos(w);
        double s0 = 0, s1 = 0, s2 = 0;
        foreach (float sample in samples)
        {
            s0 = sample + coeff * s1 - s2;
            s2 = s1;
            s1 = s0;
        }

        double real = s1 - s2 * Math.Cos(w);
        double imag = s2 * Math.Sin(w);
        return real * real + imag * imag;
    }
}
