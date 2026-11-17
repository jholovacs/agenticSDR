namespace AgenticSdr.Dsp;

/// <summary>Which program to hear from a broadcast-FM multiplex: the main channel or an SCA subcarrier.</summary>
public enum WbfmProgram
{
    Main = 0,
    Sca67 = 67,
    Sca92 = 92
}

public static class WbfmPrograms
{
    public static WbfmProgram Parse(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "sca67" or "67" or "sca-67" => WbfmProgram.Sca67,
            "sca92" or "92" or "sca-92" => WbfmProgram.Sca92,
            _ => WbfmProgram.Main
        };

    public static string WireName(WbfmProgram program) => program switch
    {
        WbfmProgram.Sca67 => "sca67",
        WbfmProgram.Sca92 => "sca92",
        _ => "main"
    };
}
