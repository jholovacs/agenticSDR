using AgenticSdr.Dsp;

namespace AgenticSdr.Processors;

/// <summary>All one-shot mode analyzers used by Signal Analyze.</summary>
public static class ModeAnalyzerCatalog
{
    private static readonly IModeAnalyzer[] All =
    [
        new CwAnalyzer(),
        new UsbAnalyzer(),
        new LsbAnalyzer(),
        new AmAnalyzer(),
        new NbfmAnalyzer(),
        new WbfmAnalyzer(),
        new Ft8Analyzer(),
        new Js8Analyzer(),
        new WsprAnalyzer(),
        new OliviaAnalyzer(),
        new JttyAnalyzer(),
        new VaracAnalyzer(),
        new PacketAnalyzer(),
        new RibbitAnalyzer(),
        new DmrAnalyzer(),
        new DstarAnalyzer(),
        new M17Analyzer()
    ];

    public static IReadOnlyList<IModeAnalyzer> Analyzers => All;

    public static IModeAnalyzer? For(RadioMode mode) =>
        All.FirstOrDefault(a => a.Mode == mode);

    /// <summary>
    /// Modes whose passband fits the measured envelope (±margin), plus any forced modes
    /// (current hit / band-plan hint) so Analyze always scores the prior guess.
    /// </summary>
    public static IReadOnlyList<IModeAnalyzer> Candidates(
        double bandwidthHz,
        RadioMode? priorMode,
        RadioMode? hintMode,
        double margin = 0.10)
    {
        var set = new HashSet<RadioMode>();
        var list = new List<IModeAnalyzer>();
        foreach (IModeAnalyzer analyzer in All)
        {
            bool fit = ModeBandwidth.FitsWithMargin(analyzer.Mode, bandwidthHz, margin);
            bool forced = analyzer.Mode == priorMode || analyzer.Mode == hintMode;
            if (!fit && !forced)
                continue;
            if (!set.Add(analyzer.Mode))
                continue;
            list.Add(analyzer);
        }

        return list;
    }
}
