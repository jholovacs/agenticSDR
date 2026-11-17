namespace AgenticSdr.Radio;

/// <summary>
/// Sample-rate and antenna choices for a LimeSDR that is staying receive-only.
/// Rates below 2.5 MS/s need the transmit DAC powered on a LimeSDR-USB, so this program uses 3.072 MS/s instead.
/// Loopback ports are rejected because they couple the transmitter into the receiver.
/// </summary>
public static class LimePlan
{
    public const double MinimumReceiveHz = 2_500_000;
    public const double VoiceHz = 3_072_000;
    public const double MaximumHz = 61_440_000;

    public readonly record struct Port(int Index, string Name, double MinimumHz, double MaximumHz);

    public static double ChooseRate(double preferredHz)
    {
        if (preferredHz >= MinimumReceiveHz && preferredHz <= MaximumHz)
            return preferredHz;
        return VoiceHz;
    }

    public static int ReceivePort(IReadOnlyList<Port> ports, double frequencyHz)
    {
        List<Port> usable = ports.Where(port => IsReceivePort(port.Name)).ToList();
        if (usable.Count == 0)
            throw new RadioException("This LimeSDR has no receive antenna port.");

        if (usable.All(port => port.MaximumHz <= port.MinimumHz))
        {
            foreach (string name in new[] { "LNAW", "LNAL", "LNAH" })
            {
                Port match = usable.FirstOrDefault(port => port.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(match.Name))
                    return match.Index;
            }

            return usable[0].Index;
        }

        List<Port> covering = usable
            .Where(port => port.MaximumHz > port.MinimumHz && frequencyHz >= port.MinimumHz && frequencyHz <= port.MaximumHz)
            .ToList();
        if (covering.Count > 0)
            return covering.MinBy(port => (port.MaximumHz - port.MinimumHz, port.Index)).Index;

        return usable.MinBy(port => (Distance(port, frequencyHz), port.Index)).Index;
    }

    public static bool IsReceivePort(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        if (name.Equals("none", StringComparison.OrdinalIgnoreCase))
            return false;
        if (name.StartsWith("LB", StringComparison.OrdinalIgnoreCase))
            return false;
        if (name.Contains("loop", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    private static double Distance(Port port, double frequencyHz)
    {
        if (port.MaximumHz <= port.MinimumHz)
            return double.MaxValue;
        if (frequencyHz < port.MinimumHz)
            return port.MinimumHz - frequencyHz;
        if (frequencyHz > port.MaximumHz)
            return frequencyHz - port.MaximumHz;
        return 0;
    }
}
