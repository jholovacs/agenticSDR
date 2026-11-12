using AgenticSdr.Host;

namespace AgenticSdr.Tests;

public class SetupTests
{
    [Fact]
    public void Installer_command_saves_the_openai_connection()
    {
        string path = Path.Combine(Path.GetTempPath(), "agentic-sdr-setup-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            bool handled = SetupCommands.TryHandle(
            [
                "--set-llm",
                "--path", path,
                "--base", "https://api.openai.com/v1",
                "--model", "gpt-4o-mini",
                "--key", "sk-installer-secret"
            ], out int code);

            Assert.True(handled);
            Assert.Equal(0, code);
            var store = new LlmStore(path);
            LlmSettings saved = store.Read();
            Assert.Equal("https://api.openai.com/v1", saved.BaseUrl);
            Assert.Equal("gpt-4o-mini", saved.Model);
            Assert.Equal("sk-installer-secret", saved.ApiKey);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void A_blank_installer_key_clears_a_saved_key()
    {
        string path = Path.Combine(Path.GetTempPath(), "agentic-sdr-setup-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            SetupCommands.WriteLlm(new SetupCommands.Options(path, "https://api.openai.com/v1", "gpt-4o-mini", "sk-old", null));
            SetupCommands.WriteLlm(new SetupCommands.Options(path, "https://api.openai.com/v1", "gpt-4o-mini", "", null));
            Assert.Equal("", new LlmStore(path).Read().ApiKey);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void SanitizeInstallerPath_strips_escaped_quote_from_directory_home()
    {
        // Simulates Windows parsing of --home "C:\Program Files\AgenticSDR\"
        string mangled = @"C:\Program Files\AgenticSDR""";
        string cleaned = SetupCommands.SanitizeInstallerPath(mangled);
        Assert.DoesNotContain("\"", cleaned);
        Assert.EndsWith("AgenticSDR", cleaned.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string native = Path.Combine(cleaned, "native");
        Assert.DoesNotContain("\"", native);
    }

    [Fact]
    public void Msi_flag_is_accepted_alongside_set_llm()
    {
        string path = Path.Combine(Path.GetTempPath(), "agentic-sdr-setup-" + Guid.NewGuid().ToString("N") + ".json");
        string log = AppPaths.SetupLog();
        try
        {
            if (File.Exists(log))
                File.Delete(log);

            bool handled = SetupCommands.TryHandle(
            [
                "--set-llm",
                "--msi",
                "--path", path,
                "--base", "https://api.openai.com/v1",
                "--model", "gpt-4o-mini",
                "--key", "sk-msi"
            ], out int code);

            Assert.True(handled);
            Assert.Equal(0, code);
            Assert.Equal("sk-msi", new LlmStore(path).Read().ApiKey);
            Assert.True(File.Exists(log));
            Assert.Contains("Finished --set-llm", File.ReadAllText(log), StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
