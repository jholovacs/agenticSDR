using AgenticSdr.Privilege;
using AgenticSdr.Shared;

namespace AgenticSdr.Host;

/// <summary>
/// Where the OpenAI connection is stored. A copy next to the program wins, so development
/// keeps using Host/llm.json. Otherwise the user's own file wins, then the system file a package wrote.
/// Operator and antenna settings use <see cref="SharedPaths"/> so mode desks share them.
/// </summary>
public static class AppPaths
{
    public const string LinuxSystemLlm = "/etc/agenticsdr/llm.json";

    /// <summary>Kept for callers that still use the old Linux-only name.</summary>
    public const string SystemLlm = LinuxSystemLlm;

    public static string ContentLlm(string contentRoot) => Path.Combine(contentRoot, "llm.json");

    public static string WindowsSystemDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "AgenticSDR");

    public static string WindowsSystemLlm() => Path.Combine(WindowsSystemDirectory(), "llm.json");

    /// <summary>Written by MSI custom actions when setup steps fail or finish.</summary>
    public static string SetupLog() =>
        OperatingSystem.IsWindows()
            ? Path.Combine(WindowsSystemDirectory(), "setup.log")
            : Path.Combine(Path.GetTempPath(), "agenticsdr-setup.log");

    public static string UserSchedule()
    {
        string llm = UserLlm();
        string? directory = Path.GetDirectoryName(llm);
        return Path.Combine(directory ?? "", "schedule.json");
    }

    public static string UserAntenna() => SharedPaths.AntennaJson();

    public static string UserOperator() => SharedPaths.OperatorJson();

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
        if (OperatingSystem.IsWindows())
        {
            string system = Path.Combine(WindowsSystemDirectory(), "callsigns.json");
            if (File.Exists(system))
                return system;
        }

        return UserCallsignCache();
    }

    public static string SelectSchedule(string contentRoot)
    {
        string content = Path.Combine(contentRoot, "schedule.json");
        if (File.Exists(content))
            return content;
        if (OperatingSystem.IsWindows())
        {
            string system = Path.Combine(WindowsSystemDirectory(), "schedule.json");
            if (File.Exists(system))
                return system;
        }

        return UserSchedule();
    }

    public static string SelectAntenna(string contentRoot)
    {
        string content = Path.Combine(contentRoot, "antenna.json");
        if (File.Exists(content))
            return content;
        if (OperatingSystem.IsWindows())
        {
            string system = Path.Combine(WindowsSystemDirectory(), "antenna.json");
            if (File.Exists(system))
                return system;
        }

        return UserAntenna();
    }

    public static string UserLlm() => SharedPaths.LlmJson();

    public static string SelectLlm(string contentRoot)
    {
        string content = ContentLlm(contentRoot);
        if (File.Exists(content))
            return content;
        string user = UserLlm();
        if (File.Exists(user))
            return user;
        if (OperatingSystem.IsWindows())
        {
            string windowsSystem = WindowsSystemLlm();
            if (File.Exists(windowsSystem))
                return windowsSystem;
        }
        else if (OperatingSystem.IsLinux() && File.Exists(LinuxSystemLlm))
            return LinuxSystemLlm;

        return user;
    }
}
