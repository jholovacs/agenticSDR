namespace AgenticSdr.Dsp;

/// <summary>
/// Occupied width used while scanning. A sideband voice channel is not measured with a broadcast-FM window,
/// and a narrow-FM voice channel is not measured as a single whistle.
/// </summary>
public static class ModeBandwidth
{
    public readonly record struct Pass(double MinHz, double MaxHz, double WidthHz, double StepHz);

    public static Pass? For(RadioMode mode) => mode switch
    {
        RadioMode.Cw => new Pass(80, 800, 400, 500),
        RadioMode.Usb or RadioMode.Lsb => new Pass(1_000, 3_500, 2_700, 1_500),
        RadioMode.Am => new Pass(4_000, 12_000, 10_000, 10_000),
        RadioMode.Nbfm or RadioMode.Dmr or RadioMode.M17 => new Pass(2_000, 16_000, 12_500, 12_500),
        RadioMode.Wbfm => new Pass(50_000, 250_000, 200_000, 200_000),
        RadioMode.Raw => new Pass(4_000, 40_000, 20_000, 12_500),
        _ => null
    };

    /// <summary>
    /// Modes to measure on one segment. Phone segments are also measured as Morse,
    /// because a keyed signal can sit in the same channel as voice.
    /// </summary>
    public static IEnumerable<RadioMode> ScanModes(RadioMode preferred)
    {
        if (preferred == RadioMode.Unknown)
        {
            yield return RadioMode.Cw;
            yield return RadioMode.Usb;
            yield return RadioMode.Nbfm;
            yield break;
        }

        if (For(preferred) is not null)
            yield return preferred;

        if (preferred is RadioMode.Usb or RadioMode.Lsb or RadioMode.Am or RadioMode.Nbfm)
            yield return RadioMode.Cw;
    }

    public static bool Fits(RadioMode mode, double bandwidthHz)
    {
        Pass? pass = For(mode);
        if (pass is null)
            return Fits(RadioMode.Cw, bandwidthHz) || Fits(RadioMode.Usb, bandwidthHz) || Fits(RadioMode.Nbfm, bandwidthHz);
        return bandwidthHz >= pass.Value.MinHz && bandwidthHz <= pass.Value.MaxHz;
    }
}
