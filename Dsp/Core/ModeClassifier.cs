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
        // Keep bandwidth gates tight so AM/LSB voice on the same kHz is not labeled as digi.
        bool amLike = peak.CarrierProminenceDb > 10 && Math.Abs(peak.Asymmetry) < 0.35 && bw > 1_500;
        if (!amLike && (hint?.PreferredMode is RadioMode.Ft8 or RadioMode.Js8) && bw < 3_600 && peak.SnrDb >= 0)
        {
            mode = hint.PreferredMode;
            confidence = peak.SnrDb >= 6 ? 0.48 : 0.40;
        }
        // Olivia is often several dB below the noise floor and still copyable — do not require SNR ≥ 0.
        else if (!amLike && hint?.PreferredMode == RadioMode.Olivia && bw < 1_500 && peak.SnrDb >= -8)
        {
            mode = RadioMode.Olivia;
            confidence = peak.SnrDb >= 6 ? 0.46 : peak.SnrDb >= 0 ? 0.38 : 0.32;
        }
        // JTTY occupies ~127 Hz; a multi-kHz AM/LSB footprint must not inherit the 7.090 dial label.
        else if (!amLike && hint?.PreferredMode == RadioMode.Jtty && bw < 800 && peak.SnrDb >= 0)
        {
            mode = RadioMode.Jtty;
            confidence = peak.SnrDb >= 6 ? 0.46 : 0.38;
        }
        // WSPR is ~6 Hz; peaks look like tiny CW. Prefer the dial even at negative SNR.
        else if (!amLike && hint?.PreferredMode == RadioMode.Wspr && bw < 800 && peak.SnrDb >= -12)
        {
            mode = RadioMode.Wspr;
            confidence = peak.SnrDb >= 6 ? 0.46 : peak.SnrDb >= 0 ? 0.38 : 0.30;
        }
        else if (!amLike && hint?.PreferredMode == RadioMode.Varac && bw < 3_200 && peak.SnrDb >= 0)
        {
            mode = RadioMode.Varac;
            confidence = peak.SnrDb >= 6 ? 0.46 : 0.38;
        }
        // Packet / Ribbit ride NBFM-looking channels on VHF/UHF — prefer the dial over generic FM.
        else if (hint?.PreferredMode == RadioMode.Packet && bw is > 4_500 and <= 20_000 && peak.SnrDb >= 0)
        {
            mode = RadioMode.Packet;
            confidence = peak.SnrDb >= 6 ? 0.52 : 0.44;
        }
        else if (hint?.PreferredMode == RadioMode.Ribbit && bw is > 4_500 and <= 20_000 && peak.SnrDb >= 0)
        {
            mode = RadioMode.Ribbit;
            confidence = peak.SnrDb >= 6 ? 0.50 : 0.42;
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

        bool digiMode = mode is RadioMode.Ft8 or RadioMode.Js8 or RadioMode.Olivia or RadioMode.Jtty
            or RadioMode.Wspr or RadioMode.Varac or RadioMode.Packet or RadioMode.Ribbit;
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
            "wspr" or "whisper" => RadioMode.Wspr,
            "varac" or "vara" or "xara" or "zara" => RadioMode.Varac,
            "wbfm" or "wfm" or "fmwb" => RadioMode.Wbfm,
            "dmr" => RadioMode.Dmr,
            "m17" => RadioMode.M17,
            "packet" or "ax25" or "aprs" or "afsk" or "pkt" => RadioMode.Packet,
            "ribbit" or "rattlegram" or "ori" => RadioMode.Ribbit,
            "raw" => RadioMode.Raw,
            _ => throw new ArgumentException($"Unknown mode '{mode}'. Use auto, nbfm, am, usb, lsb, cw, ft8, js8, olivia, jtty, wspr, varac, wbfm, dmr, m17, packet, ribbit, or raw.")
        };
    }
}
