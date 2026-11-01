namespace AgenticSdr.Dsp;

/// <summary>
/// How information was placed on the radio wave. Auto in the page maps to <see cref="Unknown"/> until a measurement picks one.
/// </summary>
public enum RadioMode
{
    /// <summary>Not chosen yet. The classifier will measure the signal.</summary>
    Unknown = 0,

    /// <summary>Narrow FM. Voice shifts the frequency by a few kilohertz. Amateur repeaters and NOAA weather.</summary>
    Nbfm,

    /// <summary>Amplitude modulation. The audio changes the strength of the wave. AM broadcast uses this.</summary>
    Am,

    /// <summary>Upper sideband voice, used on amateur HF above 10 MHz.</summary>
    Usb,

    /// <summary>Lower sideband voice, used on amateur HF below 10 MHz.</summary>
    Lsb,

    /// <summary>Continuous wave. An on-off carrier read as Morse code.</summary>
    Cw,

    /// <summary>FT8 digital messages in 15-second slots on known dial frequencies.</summary>
    Ft8,

    /// <summary>Wide FM, about 200 kHz wide. Ordinary FM broadcast from 88 to 108 MHz.</summary>
    Wbfm,

    /// <summary>DMR digital voice on VHF or UHF. The framing carries talkgroup, radio ID, and alias text. The voice codec is not turned into speech.</summary>
    Dmr,

    /// <summary>M17 digital voice on VHF or UHF. Open Codec2 speech with callsigns in the link setup.</summary>
    M17,

    /// <summary>Wide discriminator audio, without the voice filter or de-emphasis.</summary>
    Raw
}

/// <summary>
/// What the band plan expects at a frequency: a preferred mode, the segment name, and a calling-channel name when the frequency sits on one.
/// </summary>
public sealed record BandHint(RadioMode PreferredMode, string? SegmentName = null, string? CallingName = null);

/// <summary>
/// One peak in a spectrum. Frequency is the center of the hump. SNR is how far it stands above the noise, in decibels.
/// Bandwidth is the width of the hump. Asymmetry helps tell upper sideband from lower sideband.
/// PossibleImage means direct sampling may have mirrored this signal.
/// </summary>
public sealed record SpectralPeak(
    double FrequencyHz,
    double PowerDb,
    double NoiseFloorDb,
    double SnrDb,
    double BandwidthHz,
    double LowHz,
    double HighHz,
    double CarrierProminenceDb,
    double Asymmetry,
    bool PossibleImage);

/// <summary>A mode guess. Confidence runs from 0 to 1. Listening is how you check it.</summary>
public sealed record Classification(RadioMode Mode, double Confidence, double BandwidthHz, double SnrDb);
