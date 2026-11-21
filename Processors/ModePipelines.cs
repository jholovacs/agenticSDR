using AgenticSdr.Dsp;

namespace AgenticSdr.Processors;

/// <summary>Default ordered processor ids for each radio mode.</summary>
public static class ModePipelines
{
    public static IReadOnlyList<string> DefaultIds(RadioMode mode) => mode switch
    {
        RadioMode.Cw => ["morse"],
        RadioMode.Usb or RadioMode.Lsb => ["morse", "speech"],
        RadioMode.Nbfm or RadioMode.Am => ["speech"],
        RadioMode.Wbfm => ["rds", "speech"],
        RadioMode.Ft8 => ["ft8"],
        RadioMode.Js8 => ["js8"],
        RadioMode.Olivia => ["olivia"],
        RadioMode.Jtty => ["jtty"],
        RadioMode.Wspr => ["wspr"],
        RadioMode.Varac => ["varac"],
        RadioMode.Packet => ["packet", "aprs-explain"],
        RadioMode.Ribbit => ["ribbit"],
        _ => []
    };

    public static bool IsKnownId(string id) =>
        id is "morse" or "speech" or "ft8" or "js8" or "olivia" or "jtty" or "wspr" or "varac"
            or "packet" or "aprs-explain" or "ribbit" or "rds";
}
