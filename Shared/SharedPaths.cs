namespace AgenticSdr.Shared;

/// <summary>
/// User-scoped files shared by Host and every mode desk (operator, antenna, fold prefs).
/// Windows: %LocalAppData%/AgenticSDR/. Linux/macOS: ~/.config/agenticsdr/.
/// </summary>
public static class SharedPaths
{
    public static string UserConfigDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AgenticSDR");
        }

        string config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? "";
        if (string.IsNullOrWhiteSpace(config))
            config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(config, "agenticsdr");
    }

    public static string OperatorJson() => Path.Combine(UserConfigDirectory(), "operator.json");

    public static string AntennaJson() => Path.Combine(UserConfigDirectory(), "antenna.json");

    public static string RepeaterJson() => Path.Combine(UserConfigDirectory(), "repeater.json");

    public static string RadioJson() => Path.Combine(UserConfigDirectory(), "radio.json");

    /// <summary>OpenAI-compatible endpoint settings (shared by Host and every mode desk).</summary>
    public static string LlmJson() => Path.Combine(UserConfigDirectory(), "llm.json");

    /// <summary>Fold open/closed, volume, and desk dial defaults shared across apps. Not prompt history.</summary>
    public static string UiPrefsJson() => Path.Combine(UserConfigDirectory(), "ui-prefs.json");
}
