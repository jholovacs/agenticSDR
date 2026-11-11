namespace AgenticSdr.Host;

/// <summary>
/// Where the OpenAI connection is stored. A copy next to the program wins, so development
/// keeps using Host/llm.json. Otherwise the user's own file wins, then the system file a package wrote.
/// </summary>
public static class AppPaths
{
    public const string SystemLlm = "/etc/agenticsdr/llm.json";

    public static string ContentLlm(string contentRoot) => Path.Combine(contentRoot, "llm.json");

    public static string UserSchedule()
    {
        string llm = UserLlm();
        string? directory = Path.GetDirectoryName(llm);
        return Path.Combine(directory ?? "", "schedule.json");
    }

    public static string UserCallsignCache()
    {
        string llm = UserLlm();
        string? directory = Path.GetDirectoryName(llm);
        return Path.Combine(directory ?? "", "callsigns.json");
    }

    public static string SelectCallsignCache(string contentRoot)
    {
        string content = Path.Combine(contentRoot, "callsigns.json");
        if (File.Exists(content))
            return content;
        return UserCallsignCache();
    }

    public static string SelectSchedule(string contentRoot)
    {
        string content = Path.Combine(contentRoot, "schedule.json");
        if (File.Exists(content))
            return content;
        return UserSchedule();
    }

    public static string UserLlm()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AgenticSDR",
                "llm.json");
        }

        string config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? "";
        if (string.IsNullOrWhiteSpace(config))
            config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(config, "agenticsdr", "llm.json");
    }

    public static string SelectLlm(string contentRoot)
    {
        string content = ContentLlm(contentRoot);
        if (File.Exists(content))
            return content;
        string user = UserLlm();
        if (File.Exists(user))
            return user;
        if (OperatingSystem.IsLinux() && File.Exists(SystemLlm))
            return SystemLlm;
        return user;
    }
}
