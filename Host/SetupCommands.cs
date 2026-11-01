using System.IO.Compression;
using Whisper.net;
using Whisper.net.Ggml;

namespace AgenticSdr.Host;

/// <summary>
/// Installer commands. They run instead of the web server.
/// --set-llm-env reads AGENTIC_LLM_PATH, AGENTIC_LLM_BASE, AGENTIC_LLM_MODEL, and AGENTIC_LLM_KEY.
/// --apply-setup saves that connection and downloads librtlsdr plus the speech model.
/// </summary>
public static class SetupCommands
{
    public const string RtlZipUrl = "https://github.com/rtlsdrblog/rtl-sdr-blog/releases/download/V1.4.0/Release.zip";

    public static bool TryHandle(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0)
            return false;
        if (args[0] == "--set-llm-env")
        {
            exitCode = WriteFromEnvironment();
            return true;
        }

        if (args[0] != "--apply-setup" && args[0] != "--set-llm" && args[0] != "--fetch-native")
            return false;

        Options options = ReadOptions(args);
        try
        {
            if (args[0] != "--fetch-native")
                WriteLlm(options);
            if (args[0] != "--set-llm")
                FetchNative(options.Home).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or HttpRequestException or InvalidDataException)
        {
            Console.Error.WriteLine(ex.Message);
            exitCode = 1;
        }

        return true;
    }

    public static void WriteLlm(Options options)
    {
        string path = string.IsNullOrWhiteSpace(options.Path) ? AppPaths.UserLlm() : options.Path;
        var store = new LlmStore(path);
        store.Save(new LlmSave(options.BaseUrl, options.Model, options.ApiKey, string.IsNullOrWhiteSpace(options.ApiKey)));
    }

    public static async Task FetchNative(string? home)
    {
        string root = string.IsNullOrWhiteSpace(home) ? AppContext.BaseDirectory : home;
        if (OperatingSystem.IsWindows())
            await FetchRtlAsync(Path.Combine(root, "native"));
        await FetchSpeechModelAsync(Path.Combine(root, "models", "ggml-tiny.en.bin"));
    }

    private static int WriteFromEnvironment()
    {
        try
        {
            WriteLlm(new Options(
                Environment.GetEnvironmentVariable("AGENTIC_LLM_PATH"),
                Environment.GetEnvironmentVariable("AGENTIC_LLM_BASE") ?? "",
                Environment.GetEnvironmentVariable("AGENTIC_LLM_MODEL") ?? "",
                Environment.GetEnvironmentVariable("AGENTIC_LLM_KEY") ?? "",
                null));
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static Options ReadOptions(string[] args)
    {
        string? path = null;
        string? home = null;
        string baseUrl = "";
        string model = "";
        string key = "";
        for (int i = 1; i < args.Length; i++)
        {
            string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Missing a value after {args[i]}.");
            switch (args[i])
            {
                case "--path":
                    path = Value();
                    break;
                case "--home":
                    home = Value();
                    break;
                case "--base":
                    baseUrl = Value();
                    break;
                case "--model":
                    model = Value();
                    break;
                case "--key":
                    key = Value();
                    break;
                default:
                    throw new ArgumentException($"Unknown option {args[i]}.");
            }
        }

        return new Options(path, baseUrl, model, key, home);
    }

    private static async Task FetchRtlAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        string dll = Path.Combine(directory, "rtlsdr.dll");
        if (File.Exists(dll))
            return;

        string zipPath = Path.Combine(Path.GetTempPath(), "agenticsdr-rtlsdr-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            await DownloadAsync(RtlZipUrl, zipPath);
            using ZipArchive zip = ZipFile.OpenRead(zipPath);
            string[] names = ["rtlsdr.dll", "pthreadVC2.dll", "msvcr100.dll"];
            foreach (string name in names)
            {
                ZipArchiveEntry? entry = zip.Entries.FirstOrDefault(item =>
                    item.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                    item.FullName.Contains("x64", StringComparison.OrdinalIgnoreCase));
                if (entry is null)
                    throw new InvalidDataException($"The librtlsdr archive has no x64 {name}.");
                entry.ExtractToFile(Path.Combine(directory, name), overwrite: true);
            }
        }
        finally
        {
            if (File.Exists(zipPath))
                File.Delete(zipPath);
        }
    }

    private static async Task FetchSpeechModelAsync(string destination)
    {
        if (File.Exists(destination))
            return;
        string? directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        string partial = destination + ".partial";
        await using Stream downloaded = await WhisperGgmlDownloader.GetGgmlModelAsync(
            GgmlType.TinyEn, QuantizationType.NoQuantization, CancellationToken.None);
        await using (FileStream file = File.Create(partial))
            await downloaded.CopyToAsync(file);
        File.Move(partial, destination, overwrite: true);
    }

    private static async Task DownloadAsync(string url, string destination)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AgenticSdr/0.1");
        await using Stream remote = await http.GetStreamAsync(url);
        await using FileStream file = File.Create(destination);
        await remote.CopyToAsync(file);
    }

    public sealed record Options(string? Path, string BaseUrl, string Model, string ApiKey, string? Home);
}
