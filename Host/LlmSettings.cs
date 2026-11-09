using System.Text.Json;

namespace AgenticSdr.Host;

/// <summary>
/// An OpenAI-compatible instruct model. BaseUrl is the address that ends in /v1.
/// A bare host like http://host:11434 is normalized to …/v1 for Ollama and similar servers.
/// ApiKey is sent only to that address, as a bearer token. Model is the name the server expects.
/// </summary>
public sealed record LlmSettings(string BaseUrl, string Model, string ApiKey)
{
    public string ChatUrl => $"{BaseUrl}/chat/completions";
    public string ModelsUrl => $"{BaseUrl}/models";

    /// <summary>Ollama's native model list, one path above the OpenAI /v1 root.</summary>
    public string OllamaTagsUrl
    {
        get
        {
            string root = BaseUrl;
            if (root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                root = root[..^3];
            return $"{root.TrimEnd('/')}/api/tags";
        }
    }
}

/// <summary>
/// The settings the page is allowed to see. The key itself is never sent back.
/// HasKey is true when a key is stored. KeyHint is the last four characters, so you can tell which key is saved.
/// </summary>
public sealed record LlmPublic(string BaseUrl, string Model, bool HasKey, string KeyHint);

/// <summary>Body of PUT /api/llm. An empty API key keeps the saved one. ClearKey forgets it.</summary>
public sealed record LlmSave(string? BaseUrl, string? Model, string? ApiKey, bool ClearKey);

/// <summary>
/// Reads and writes Host/llm.json. That file is gitignored because it can hold an API key.
/// </summary>
public sealed class LlmStore(string path)
{
    private readonly object _gate = new();
    private string _path = path;

    public LlmSettings Read()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
                return new LlmSettings("", "", "");
            try
            {
                var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(_path), JsonOptions()) ?? new Saved();
                return new LlmSettings(saved.BaseUrl ?? "", saved.Model ?? "", saved.ApiKey ?? "");
            }
            catch (JsonException)
            {
                return new LlmSettings("", "", "");
            }
        }
    }

    public LlmPublic View() => Present(Read());

    public LlmPublic Save(LlmSave body)
    {
        lock (_gate)
        {
            LlmSettings current = Read();
            string baseUrl = NormalizeBase(body.BaseUrl ?? "");
            string model = (body.Model ?? "").Trim();
            if (model.Length > 200)
                throw new ArgumentException("The model name is too long.");
            string key = body.ClearKey
                ? ""
                : string.IsNullOrWhiteSpace(body.ApiKey) ? current.ApiKey : body.ApiKey.Trim();
            if (key.Length > 512)
                throw new ArgumentException("The API key is too long.");
            var next = new LlmSettings(baseUrl, model, key);
            try
            {
                Write(next);
            }
            catch (UnauthorizedAccessException) when (_path != AppPaths.UserLlm())
            {
                _path = AppPaths.UserLlm();
                Write(next);
            }

            return Present(next);
        }
    }

    private void Write(LlmSettings next)
    {
        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(_path, JsonSerializer.Serialize(new Saved
        {
            BaseUrl = next.BaseUrl,
            Model = next.Model,
            ApiKey = next.ApiKey
        }, JsonOptions()));
    }

    public static string NormalizeBase(string raw)
    {
        string url = raw.Trim().TrimEnd('/');
        if (url.Length == 0)
            return "";
        const string chat = "/chat/completions";
        if (url.EndsWith(chat, StringComparison.OrdinalIgnoreCase))
            url = url[..^chat.Length].TrimEnd('/');
        if (url.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
            url = url[..^"/models".Length].TrimEnd('/');
        if (url.EndsWith("/api/tags", StringComparison.OrdinalIgnoreCase))
            url = url[..^"/api/tags".Length].TrimEnd('/');
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) ||
            parsed.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(parsed.UserInfo))
            throw new ArgumentException("The endpoint must be an http or https address. For Ollama use http://host:11434 or …/v1. Put any key in the API key field.");

        // Bare Ollama (and similar) roots need the OpenAI /v1 path.
        string path = parsed.AbsolutePath.TrimEnd('/');
        if (path.Length == 0)
            url += "/v1";
        return url;
    }

    private static LlmPublic Present(LlmSettings settings)
    {
        bool has = settings.ApiKey.Length > 0;
        string hint = has ? settings.ApiKey[^Math.Min(4, settings.ApiKey.Length)..] : "";
        return new LlmPublic(settings.BaseUrl, settings.Model, has, hint);
    }

    private static JsonSerializerOptions JsonOptions() => new() { WriteIndented = true };

    private sealed class Saved
    {
        public string? BaseUrl { get; set; }
        public string? Model { get; set; }
        public string? ApiKey { get; set; }
    }
}
