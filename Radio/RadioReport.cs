namespace AgenticSdr.Radio;

/// <summary>One labeled fact about a radio, such as its firmware version or tuning range.</summary>
public sealed record RadioFact(string Label, string Value);

/// <summary>The name and the facts a Radio Details view can show.</summary>
public sealed record RadioReport(string Title, IReadOnlyList<RadioFact> Facts);

/// <summary>Formats frequencies and collects the facts that are known before a radio is opened.</summary>
public static class RadioText
{
    public static string Hertz(double hz)
    {
        double abs = Math.Abs(hz);
        if (abs >= 1_000_000_000)
            return $"{hz / 1_000_000_000:0.###} GHz";
        if (abs >= 1_000_000)
            return $"{hz / 1_000_000:0.###} MHz";
        if (abs >= 1_000)
            return $"{hz / 1_000:0.#} kHz";
        return $"{hz:0} Hz";
    }

    public static string Range(double minimumHz, double maximumHz) => $"{Hertz(minimumHz)} – {Hertz(maximumHz)}";

    public static void Add(List<RadioFact> facts, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        facts.Add(new RadioFact(label, value.Trim()));
    }

    public static void Identity(List<RadioFact> facts, RadioDeviceInfo info)
    {
        Add(facts, "Name", info.Name);
        Add(facts, "Manufacturer", info.Manufacturer);
        Add(facts, "Product", info.Product);
        Add(facts, "Serial", info.Serial);
        Add(facts, "Connection", info.Driver);
    }
}
