namespace AgenticSdr.Dsp;

/// <summary>
/// Guesses the mode from the width and shape of a peak, then checks that guess against the band plan.
/// A very narrow peak is Morse unless it sits on an FT8 dial. A very wide peak on the FM broadcast band is WBFM.
/// </summary>
public static class ModeClassifier
{
    public static Classification Classify(SpectralPeak peak, BandHint? hint)
    {
        double bw = peak.BandwidthHz;
        RadioMode mode = RadioMode.Unknown;
        double confidence = 0.3;

        // Digi dial hints mean “this looks like the published watering hole,” not decode certainty.
        if ((hint?.PreferredMode is RadioMode.Ft8 or RadioMode.Js8) && bw < 3_600 && peak.SnrDb >= 0)
        {
            mode = hint.PreferredMode;
            confidence = peak.SnrDb >= 6 ? 0.48 : 0.40;
        }
        else if (hint?.PreferredMode == RadioMode.Olivia && bw < 2_500 && peak.SnrDb >= 0)
        {
            mode = RadioMode.Olivia;
            confidence = peak.SnrDb >= 6 ? 0.46 : 0.38;
        }
        else if (hint?.PreferredMode == RadioMode.Jtty && bw < 3_000 && peak.SnrDb >= 0)
        {
            mode = RadioMode.Jtty;
            confidence = peak.SnrDb >= 6 ? 0.46 : 0.38;
        }
        else if (hint?.PreferredMode == RadioMode.Varac && bw < 3_200 && peak.SnrDb >= 0)
        {
            mode = RadioMode.Varac;
            confidence = peak.SnrDb >= 6 ? 0.46 : 0.38;
        }
        else if (bw < 1_000 && peak.SnrDb >= 8)
        {
            mode = RadioMode.Cw;
            confidence = 0.82;
        }
        else if (bw is >= 1_400 and <= 4_500)
        {
            if (peak.Asymmetry > 0.3)
            {
                mode = RadioMode.Usb;
                confidence = 0.74;
            }
            else if (peak.Asymmetry < -0.3)
            {
                mode = RadioMode.Lsb;
                confidence = 0.74;
            }
            else if (hint?.PreferredMode is RadioMode.Usb or RadioMode.Lsb)
            {
                mode = hint.PreferredMode;
                confidence = 0.66;
            }
            else
            {
                mode = SideFromFrequency(peak.FrequencyHz);
                confidence = 0.55;
            }
        }
        else if (bw is >= 30_000 and <= 300_000 && hint?.PreferredMode == RadioMode.Wbfm)
        {
            mode = RadioMode.Wbfm;
            confidence = 0.84;
        }
        else if (bw is > 4_500 and <= 20_000)
        {
            if (peak.CarrierProminenceDb > 10 && Math.Abs(peak.Asymmetry) < 0.35)
            {
                mode = RadioMode.Am;
                confidence = 0.72;
            }
            else
            {
                mode = RadioMode.Nbfm;
                confidence = 0.78;
            }
        }
        else if (hint is not null && hint.PreferredMode != RadioMode.Unknown)
        {
            mode = hint.PreferredMode;
            confidence = 0.4;
        }

        bool digiMode = mode is RadioMode.Ft8 or RadioMode.Js8 or RadioMode.Olivia or RadioMode.Jtty or RadioMode.Varac;
        if (hint is not null && hint.PreferredMode == mode && mode != RadioMode.Unknown && !digiMode)
            confidence = Math.Min(0.95, confidence + 0.1);
        else if (hint is not null && hint.PreferredMode == mode && digiMode)
            confidence = Math.Min(0.55, confidence + 0.04);

        if (peak.PossibleImage)
            confidence = Math.Min(confidence, 0.6);

        return new Classification(mode, confidence, bw, peak.SnrDb);
    }

    public static double CarrierFrequency(SpectralPeak peak, RadioMode mode) => mode switch
    {
        RadioMode.Usb => peak.LowHz,
        RadioMode.Lsb => peak.HighHz,
        _ => peak.FrequencyHz
    };

    public static RadioMode SideFromFrequency(double frequencyHz) =>
        frequencyHz < 10_000_000 ? RadioMode.Lsb : RadioMode.Usb;

    public static RadioMode Parse(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode) || mode.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return RadioMode.Unknown;

        return mode.Trim().ToLowerInvariant() switch
        {
            "nbfm" or "fm" or "nfm" or "fmnb" => RadioMode.Nbfm,
            "am" => RadioMode.Am,
            "usb" => RadioMode.Usb,
            "lsb" => RadioMode.Lsb,
            "cw" or "morse" => RadioMode.Cw,
            "ft8" => RadioMode.Ft8,
            "js8" or "js8call" => RadioMode.Js8,
            "olivia" => RadioMode.Olivia,
            "jtty" => RadioMode.Jtty,
            "varac" or "vara" or "xara" or "zara" => RadioMode.Varac,
            "wbfm" or "wfm" or "fmwb" => RadioMode.Wbfm,
            "dmr" => RadioMode.Dmr,
            "m17" => RadioMode.M17,
            "packet" or "ax25" or "aprs" or "afsk" or "pkt" => RadioMode.Packet,
            "raw" => RadioMode.Raw,
            _ => throw new ArgumentException($"Unknown mode '{mode}'. Use auto, nbfm, am, usb, lsb, cw, ft8, js8, olivia, jtty, varac, wbfm, dmr, m17, packet, or raw.")
        };
    }
}
