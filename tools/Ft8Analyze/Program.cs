using System.Buffers.Binary;
using AgenticSdr.Dsp;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: Ft8Analyze <wav>");
    return 1;
}

string path = args[0];
byte[] file = File.ReadAllBytes(path);
if (file.Length < 44)
{
    Console.Error.WriteLine("not wav");
    return 2;
}

int channels = BinaryPrimitives.ReadInt16LittleEndian(file.AsSpan(22));
int rate = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(24));
int bits = BinaryPrimitives.ReadInt16LittleEndian(file.AsSpan(34));
int dataOfs = 12;
while (dataOfs + 8 <= file.Length)
{
    string tag = System.Text.Encoding.ASCII.GetString(file, dataOfs, 4);
    int size = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(dataOfs + 4));
    if (tag == "data")
    {
        dataOfs += 8;
        break;
    }

    dataOfs += 8 + size;
}

int bytesPer = Math.Max(1, bits / 8 * channels);
int frames = (file.Length - dataOfs) / bytesPer;
var mono = new float[frames];
for (int i = 0; i < frames; i++)
{
    int at = dataOfs + i * bytesPer;
    float s;
    if (bits == 16)
    {
        s = BinaryPrimitives.ReadInt16LittleEndian(file.AsSpan(at)) / 32768f;
        if (channels > 1)
        {
            float r = BinaryPrimitives.ReadInt16LittleEndian(file.AsSpan(at + 2)) / 32768f;
            s = 0.5f * (s + r);
        }
    }
    else
    {
        s = file[at] / 128f - 1f;
    }

    mono[i] = s;
}

double rms = Math.Sqrt(mono.Average(v => (double)v * v));
double peak = mono.Max(v => Math.Abs(v));
Console.WriteLine($"wav rate={rate} ch={channels} bits={bits} frames={frames} dur={frames / (double)rate:F2}s rms={rms:F5} peak={peak:F4}");

int n = 1 << (int)Math.Floor(Math.Log2(Math.Min(frames, 65_536)));
var re = new double[n];
var im = new double[n];
for (int i = 0; i < n; i++)
    re[i] = mono[i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / Math.Max(1, n - 1)));
Fft(re, im);
var db = new double[n / 2];
for (int i = 0; i < db.Length; i++)
{
    double p = re[i] * re[i] + im[i] * im[i];
    db[i] = 10 * Math.Log10(p + 1e-20);
}

double noise = Percentile(db.Where((_, i) =>
{
    double hz = i * rate / (double)n;
    return hz is >= 200 and <= 3000;
}).ToArray(), 0.2);
Console.WriteLine($"fft n={n} bin={rate / (double)n:F2}Hz window_noise_p20={noise:F1} dB");

var hot = new List<(double Hz, double Db)>();
for (int i = 0; i < db.Length; i++)
{
    double hz = i * rate / (double)n;
    if (hz < 200 || hz > 3000)
        continue;
    if (db[i] >= noise + 3)
        hot.Add((hz, db[i] - noise));
}

hot.Sort((a, b) => b.Db.CompareTo(a.Db));
Console.WriteLine($"hotbins>noise+3dB in 200-3000Hz: {hot.Count}");
foreach ((double hz, double snr) in hot.Take(15))
    Console.WriteLine($"  {hz,7:F0} Hz  +{snr:F1} dB");

if (hot.Count > 0)
{
    Console.WriteLine($"mean_hot_excess={hot.Average(h => h.Db):F1} dB  top={hot[0].Db:F1} dB");
    double peakHz = hot[0].Hz;
    double peakDb = hot[0].Db + noise;
    // Width of the dominant blob (bins within 10 dB of the peak).
    double lo = peakHz, hi = peakHz;
    for (int i = 0; i < db.Length; i++)
    {
        double hz = i * rate / (double)n;
        if (hz < 200 || hz > 3000)
            continue;
        if (db[i] >= peakDb - 10)
        {
            lo = Math.Min(lo, hz);
            hi = Math.Max(hi, hz);
        }
    }

    Console.WriteLine($"dominant_blob_peak={peakHz:F0}Hz width_at_-10dB={hi - lo:F0}Hz (FT8 tone stack is ~44Hz; a CW spur is much narrower)");
}

// Symbol-rate hopping test around the strongest tone: FT8 jumps among 8 bins; a carrier does not.
{
    double peakHz = hot.Count > 0 ? hot[0].Hz : 1500;
    int sps = (int)Math.Round(rate * 0.16); // FT8 symbol
    var tonePowers = new List<double>[8];
    for (int t = 0; t < 8; t++)
        tonePowers[t] = [];
    int symbols = Math.Min(79, (frames - sps) / sps);
    for (int s = 0; s < symbols; s++)
    {
        int start = s * sps;
        var powers = new double[8];
        for (int t = 0; t < 8; t++)
        {
            double f = peakHz - 3 * 6.25 + t * 6.25;
            powers[t] = Goertzel(mono.AsSpan(start, sps), rate, f);
        }

        int best = 0;
        for (int t = 1; t < 8; t++)
            if (powers[t] > powers[best])
                best = t;
        for (int t = 0; t < 8; t++)
            tonePowers[t].Add(powers[t]);
        _ = best;
    }

    var winners = new int[8];
    for (int s = 0; s < symbols; s++)
    {
        int best = 0;
        double bestP = double.NegativeInfinity;
        for (int t = 0; t < 8; t++)
        {
            if (tonePowers[t][s] > bestP)
            {
                bestP = tonePowers[t][s];
                best = t;
            }
        }

        winners[best]++;
    }

    double entropy = 0;
    for (int t = 0; t < 8; t++)
    {
        if (winners[t] == 0)
            continue;
        double p = winners[t] / (double)symbols;
        entropy -= p * Math.Log(p, 2);
    }

    Console.WriteLine($"symbol_tone_winners_around_{peakHz:F0}Hz: [{string.Join(",", winners)}] entropy={entropy:F2} bits (FT8 ~2.5-3; CW spur ~0)");
    if (entropy < 1.0)
        Console.WriteLine("verdict_hint=dominant_energy_looks_like_carrier_or_spur_not_ft8_8fsk");
    else if (entropy > 2.0)
        Console.WriteLine("verdict_hint=tone_hopping_looks_ft8_like_decoder_sync_or_crc_is_the_bottleneck");
}

// 1-second spectrogram occupancy in FT8 slot body (skip first 0.5s)
int secSamples = rate;
int secs = Math.Min(15, frames / secSamples);
Console.WriteLine($"per_second_hotbins (200-3000, >p20+3):");
for (int sec = 0; sec < secs; sec++)
{
    int start = sec * secSamples;
    int len = Math.Min(secSamples, frames - start);
    int sn = 1 << (int)Math.Floor(Math.Log2(Math.Min(len, 8192)));
    var sre = new double[sn];
    var sim = new double[sn];
    for (int i = 0; i < sn; i++)
        sre[i] = mono[start + i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / Math.Max(1, sn - 1)));
    Fft(sre, sim);
    var sdb = new double[sn / 2];
    for (int i = 0; i < sdb.Length; i++)
        sdb[i] = 10 * Math.Log10(sre[i] * sre[i] + sim[i] * sim[i] + 1e-20);
    double snNoise = Percentile(sdb, 0.2);
    int hotCount = 0;
    for (int i = 0; i < sdb.Length; i++)
    {
        double hz = i * rate / (double)sn;
        if (hz is >= 200 and <= 3000 && sdb[i] >= snNoise + 3)
            hotCount++;
    }

    Console.WriteLine($"  t={sec,2}s hot={hotCount,3} noise={snNoise,6:F1}");
}

Ft8DecodeStats stats = Ft8Decoder.DecodeDetailed(mono, rate);
Console.WriteLine($"ft8_decode_count={stats.Messages.Count} candidates={stats.CandidatesTried} ldpc_ok={stats.LdpcAccepted} best_sync={stats.BestSyncScore:F2} audio_s={stats.AudioSeconds:F2}");
foreach (Ft8Message m in stats.Messages)
    Console.WriteLine($"  {m.SnrDb,5:F0} dB {m.AudioHz,6:F0} Hz t={m.TimeSeconds:F2}  {m.Text}");

// Extra DT pads (DecodeDetailed already sweeps ppm / Hilbert / mirror when empty).
foreach (double shiftSec in new[] { 0.0, 0.32, 0.64, 1.0, 1.5 })
{
    float[] tweaked = mono;
    int shift = (int)Math.Round(shiftSec * rate);
    if (shift > 0)
    {
        if (mono.Length <= shift)
            continue;
        tweaked = new float[mono.Length - shift];
        Array.Copy(mono, shift, tweaked, 0, tweaked.Length);
    }

    if (tweaked.Length < rate * 10)
        continue;
    Ft8DecodeStats alt = Ft8Decoder.DecodeDetailed(tweaked, rate);
    bool interesting = alt.Messages.Count > 0
        || alt.LdpcAccepted > 0
        || alt.BestSyncScore > stats.BestSyncScore + 0.5;
    if (interesting || Math.Abs(shiftSec) < 0.01)
        Console.WriteLine($"dt={shiftSec:0.00}s decode={alt.Messages.Count} ldpc_ok={alt.LdpcAccepted} best_sync={alt.BestSyncScore:F2}");
}

Console.WriteLine($"js8_decode_lines={Js8Decoder.Decode(mono, rate).Count}");

// Sanity: our own FT8 under this clip's noise floor should still decode.
{
    var probe = (float[])mono.Clone();
    for (int i = 0; i < probe.Length; i++)
        probe[i] *= 0.15f;
    Ft8Decoder.Modulate(probe, rate, "CQ K1ABC FN42", 1_200, 0.5);
    Ft8DecodeStats self = Ft8Decoder.DecodeDetailed(probe, rate);
    Console.WriteLine($"self_test_modulate_into_clip decode={self.Messages.Count} best_sync={self.BestSyncScore:F2} ldpc_ok={self.LdpcAccepted}");
}

if (stats.Messages.Count == 0)
{
    Console.WriteLine("decode_empty=true");
    if (frames < rate * 10)
        Console.WriteLine("hint=audio_shorter_than_10s");
    if (rms < 0.002)
        Console.WriteLine("hint=very_low_audio_level");
    if (stats.CandidatesTried == 0)
        Console.WriteLine("hint=no_costas_sync_candidates");
    else if (stats.LdpcAccepted == 0)
        Console.WriteLine("hint=sync_candidates_found_but_ldpc_crc_failed");
    else
        Console.WriteLine("hint=ldpc_ok_but_unpack_empty");
}

return 0;

static double Percentile(double[] values, double p)
{
    if (values.Length == 0)
        return -120;
    var a = (double[])values.Clone();
    Array.Sort(a);
    double idx = Math.Clamp(p, 0, 1) * (a.Length - 1);
    int lo = (int)idx;
    int hi = Math.Min(lo + 1, a.Length - 1);
    double f = idx - lo;
    return a[lo] * (1 - f) + a[hi] * f;
}

static float[] ShiftAudio(float[] input, int sampleRate, double freqShiftHz)
{
    if (Math.Abs(freqShiftHz) < 1e-6)
        return (float[])input.Clone();
    var output = new float[input.Length];
    double step = 2 * Math.PI * freqShiftHz / sampleRate;
    double phase = 0;
    for (int i = 0; i < input.Length; i++)
    {
        // Real audio: mix with cos to shift spectrum (USB baseband approximation).
        output[i] = input[i] * (float)Math.Cos(phase);
        phase += step;
        if (phase > Math.PI)
            phase -= 2 * Math.PI;
        else if (phase < -Math.PI)
            phase += 2 * Math.PI;
    }

    return output;
}

static double Goertzel(ReadOnlySpan<float> samples, int sampleRate, double frequencyHz)
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

static void Fft(double[] re, double[] im)
{
    int n = re.Length;
    int j = 0;
    for (int i = 1; i < n; i++)
    {
        int bit = n >> 1;
        for (; (j & bit) != 0; bit >>= 1)
            j ^= bit;
        j ^= bit;
        if (i >= j)
            continue;
        (re[i], re[j]) = (re[j], re[i]);
        (im[i], im[j]) = (im[j], im[i]);
    }

    for (int len = 2; len <= n; len <<= 1)
    {
        double ang = -2 * Math.PI / len;
        double wlenRe = Math.Cos(ang);
        double wlenIm = Math.Sin(ang);
        for (int i = 0; i < n; i += len)
        {
            double wRe = 1;
            double wIm = 0;
            for (int k = 0; k < len / 2; k++)
            {
                int u = i + k;
                int v = i + k + len / 2;
                double tRe = wRe * re[v] - wIm * im[v];
                double tIm = wRe * im[v] + wIm * re[v];
                re[v] = re[u] - tRe;
                im[v] = im[u] - tIm;
                re[u] += tRe;
                im[u] += tIm;
                double nwRe = wRe * wlenRe - wIm * wlenIm;
                wIm = wRe * wlenIm + wIm * wlenRe;
                wRe = nwRe;
            }
        }
    }
}
