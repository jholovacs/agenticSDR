namespace AgenticSdr.Dsp;

/// <summary>
/// Compensates for RTL/SDR crystal and reporting error on fixed dials.
/// After USB demod, an LO error of Δf appears as every audio tone shifted by −Δf.
/// Modes that search a wide waterfall (FT8/JS8/JTTY) already tolerate that; modes that
/// assume a fixed audio center (Olivia, VarAC) need an estimate and a retune of the stack.
/// Coarse LO correction still belongs in <c>Sdr:Ppm</c>; this handles the residual.
/// </summary>
public static class DialFrequency
{
    /// <summary>How far a typical uncalibrated dongle can sit off a published HF dial in audio Hz.</summary>
    public const double DefaultSearchHz = 400;

    /// <summary>
    /// Find the audio-frequency offset of a tone stack around <paramref name="expectedCenterHz"/>.
    /// Returns Δ such that the band energy peaks near expectedCenter + Δ.
    /// </summary>
    public static double EstimateBandOffsetHz(
        ReadOnlySpan<float> wave,
        int sampleRate,
        double expectedCenterHz,
        double halfBandwidthHz,
        double searchHz = DefaultSearchHz,
        double stepHz = 25)
    {
        if (wave.Length < sampleRate / 4 || sampleRate < 1_000 || halfBandwidthHz < 20)
            return 0;

        int n = Math.Min(wave.Length, sampleRate * 4);
        int fftSize = 1;
        while (fftSize < n && fftSize < 16_384)
            fftSize <<= 1;
        if (fftSize < 256)
            return 0;

        double[] power = SpectrumPower(wave[..n], fftSize);
        double binHz = sampleRate / (double)fftSize;
        double best = 0;
        double bestScore = double.NegativeInfinity;
        for (double delta = -searchHz; delta <= searchHz + 0.5 * stepHz; delta += stepHz)
        {
            double center = expectedCenterHz + delta;
            double lo = center - halfBandwidthHz;
            double hi = center + halfBandwidthHz;
            if (lo < 80 || hi > sampleRate * 0.48)
                continue;
            double score = BandPower(power, binHz, lo, hi);
            // Prefer a compact stack: subtract a little wideband skirt so broadband noise loses.
            score -= 0.35 * BandPower(power, binHz, lo - halfBandwidthHz, hi + halfBandwidthHz);
            if (score > bestScore)
            {
                bestScore = score;
                best = delta;
            }
        }

        return best;
    }

    /// <summary>Centers to try after an offset estimate (estimate first, then small polish steps).</summary>
    public static IReadOnlyList<double> CenterCandidates(
        double expectedCenterHz,
        double estimatedOffsetHz,
        double polishHz = 50,
        double polishStepHz = 25)
    {
        var list = new List<double>();
        var seen = new HashSet<int>();
        void Offer(double hz)
        {
            int key = (int)Math.Round(hz);
            if (!seen.Add(key))
                return;
            list.Add(hz);
        }

        Offer(expectedCenterHz + estimatedOffsetHz);
        for (double d = polishStepHz; d <= polishHz + 0.5 * polishStepHz; d += polishStepHz)
        {
            Offer(expectedCenterHz + estimatedOffsetHz - d);
            Offer(expectedCenterHz + estimatedOffsetHz + d);
        }

        // Fallbacks when the estimator latched onto the wrong bump.
        Offer(expectedCenterHz);
        Offer(expectedCenterHz - 100);
        Offer(expectedCenterHz + 100);
        return list;
    }

    /// <summary>Frequency-shift real audio via a cheap analytic (Hilbert) mix.</summary>
    public static float[] ShiftHz(float[] wave, int sampleRate, double shiftHz)
    {
        if (Math.Abs(shiftHz) < 0.5 || wave.Length == 0)
            return wave;

        ReadOnlySpan<float> taps =
        [
            0.0025f, 0, 0.0124f, 0, 0.0375f, 0, 0.0834f, 0, 0.186f, 0, 0.627f, 0,
            -0.627f, 0, -0.186f, 0, -0.0834f, 0, -0.0375f, 0, -0.0124f, 0, -0.0025f
        ];
        int n = taps.Length;
        int delay = n / 2;
        var output = new float[wave.Length];
        double step = 2 * Math.PI * shiftHz / sampleRate;
        double phase = 0;
        for (int i = 0; i < wave.Length; i++)
        {
            double imag = 0;
            for (int t = 0; t < n; t++)
            {
                int at = i - t;
                if ((uint)at < (uint)wave.Length)
                    imag += wave[at] * taps[t];
            }

            int reAt = i - delay;
            double real = (uint)reAt < (uint)wave.Length ? wave[reAt] : 0;
            output[i] = (float)(real * Math.Cos(phase) - imag * Math.Sin(phase));
            phase += step;
            if (phase > Math.PI)
                phase -= 2 * Math.PI;
            else if (phase < -Math.PI)
                phase += 2 * Math.PI;
        }

        return output;
    }

    /// <summary>
    /// Move a fixed-center modem's audio so <paramref name="expectedCenterHz"/> sits on the stack.
    /// Returns the wave (possibly shifted) and the offset that was removed.
    /// </summary>
    public static (float[] Wave, double OffsetHz) AlignToCenter(
        float[] wave,
        int sampleRate,
        double expectedCenterHz,
        double halfBandwidthHz,
        double searchHz = DefaultSearchHz)
    {
        double offset = EstimateBandOffsetHz(wave, sampleRate, expectedCenterHz, halfBandwidthHz, searchHz);
        if (Math.Abs(offset) < 8)
            return (wave, 0);
        // Shift by −offset so energy that sat at expected+offset moves to expected.
        return (ShiftHz(wave, sampleRate, -offset), offset);
    }

    /// <summary>
    /// How far to hunt around a published RF when snapping a known channel (NOAA, CB, digi dial, …).
    /// Wider on broadcast FM; a few kilohertz on NBFM/AM; digi USB uses the audio residual window.
    /// </summary>
    public static double PublishedSearchHz(RadioMode mode, double publishedHz = 0) => mode switch
    {
        RadioMode.Wbfm => 75_000,
        RadioMode.Am => 5_000,
        // ISS LEO needs a wider hunt than terrestrial NBFM/packet (Doppler ± a few kHz / ±10 kHz).
        RadioMode.Nbfm or RadioMode.Dmr or RadioMode.M17 or RadioMode.Packet or RadioMode.Ribbit
            when publishedHz > 0 => Math.Max(5_000, DopplerFilter.HalfWindowHz(publishedHz)),
        RadioMode.Nbfm or RadioMode.Dmr or RadioMode.M17 or RadioMode.Packet or RadioMode.Ribbit => 5_000,
        RadioMode.Cw => DefaultSearchHz,
        RadioMode.Usb or RadioMode.Lsb => 1_000,
        RadioMode.Ft8 or RadioMode.Js8 or RadioMode.Olivia or RadioMode.Jtty or RadioMode.Wspr or RadioMode.Varac => DefaultSearchHz,
        _ => 5_000
    };

    /// <summary>
    /// Expected RF of the strongest part of a published station: carrier for voice/FM, dial+audio-center for USB digi.
    /// </summary>
    public static double ExpectedRfHz(double publishedHz, RadioMode mode) => mode switch
    {
        // Olivia cursors are often 1000 or 1500 Hz above dial; aim at the middle of that range.
        RadioMode.Olivia => publishedHz + OliviaDecoder.CenterHz,
        RadioMode.Wspr => publishedHz + WsprDecoder.CenterHz,
        RadioMode.Varac => publishedHz + 1_500,
        RadioMode.Jtty => publishedHz + 1_500,
        RadioMode.Ft8 or RadioMode.Js8 => publishedHz + 1_500,
        _ => publishedHz
    };

    /// <summary>
    /// Snap a published frequency to the loudest RF bin near the expected carrier / digi center.
    /// Returns the corrected tune (dial for digi, carrier for voice/FM) or the published value when nothing is heard.
    /// </summary>
    public static double SnapPublishedTuneHz(
        double[] powerDb,
        double spectrumCenterHz,
        double sampleRate,
        double publishedHz,
        RadioMode mode,
        double minSnrDb = 3)
    {
        double searchHz = PublishedSearchHz(mode, publishedHz);
        double expectedRf = ExpectedRfHz(publishedHz, mode);
        double halfWidth = mode switch
        {
            // Cover both +1000 and +1500 Hz cursors without sliding into the FT8 dial above.
            RadioMode.Olivia => 500 + searchHz,
            RadioMode.Wspr => 200 + searchHz,
            RadioMode.Jtty => JttyDecoder.SpanHz + searchHz,
            RadioMode.Varac => 1_150 + searchHz,
            RadioMode.Ft8 or RadioMode.Js8 => 1_400 + searchHz,
            RadioMode.Wbfm => 100_000,
            RadioMode.Am => 5_000,
            RadioMode.Cw => 400,
            _ => Math.Max(ChannelActivity.WidthHz(mode) * 0.5, searchHz)
        };

        SpectralPeak? peak = PeakPicker.LoudestIn(
            powerDb, spectrumCenterHz, sampleRate, expectedRf, halfWidth, minSnrDb);
        if (peak is null)
            return publishedHz;

        double delta = peak.FrequencyHz - expectedRf;
        if (Math.Abs(delta) > searchHz * 2)
            return publishedHz;
        // Digi: shift the dial so dial+audio-center lands on the measured stack.
        // Voice/FM: retune onto the measured carrier.
        // Clamp Olivia so FT8 just above 7.074 cannot pull the 7.0715 dial away.
        if (mode == RadioMode.Olivia)
            delta = Math.Clamp(delta, -searchHz, searchHz);
        return publishedHz + delta;
    }

    private static double[] SpectrumPower(ReadOnlySpan<float> wave, int fftSize)
    {
        var re = new double[fftSize];
        var im = new double[fftSize];
        int copy = Math.Min(wave.Length, fftSize);
        for (int i = 0; i < copy; i++)
        {
            double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (copy - 1));
            re[i] = wave[i] * w;
        }

        Fft(re, im);
        int bins = fftSize / 2;
        var power = new double[bins];
        for (int i = 0; i < bins; i++)
            power[i] = re[i] * re[i] + im[i] * im[i];
        return power;
    }

    private static double BandPower(double[] power, double binHz, double loHz, double hiHz)
    {
        int a = Math.Clamp((int)(loHz / binHz), 0, power.Length - 1);
        int b = Math.Clamp((int)(hiHz / binHz), a, power.Length - 1);
        double sum = 0;
        for (int i = a; i <= b; i++)
            sum += power[i];
        return sum;
    }

    private static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
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
                int half = len >> 1;
                for (int j = 0; j < half; j++)
                {
                    int u = i + j;
                    int v = u + half;
                    double tRe = wRe * re[v] - wIm * im[v];
                    double tIm = wRe * im[v] + wIm * re[v];
                    re[v] = re[u] - tRe;
                    im[v] = im[u] - tIm;
                    re[u] += tRe;
                    im[u] += tIm;
                    double nextRe = wRe * wlenRe - wIm * wlenIm;
                    wIm = wRe * wlenIm + wIm * wlenRe;
                    wRe = nextRe;
                }
            }
        }
    }
}
