using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgenticSdr.Host;

/// <summary>
/// Talks to an instruct model with the OpenAI chat-completions protocol, then runs any tool the model requests.
/// A server that rejects the tools field is asked again in plain text, which older instruct models can follow.
/// </summary>
public sealed class InstructAgent(LlmStore store, RadioSession session, CampBook camp, HttpClient http)
{
    private const int MaxRounds = 6;
    private readonly AgentTools _tools = new(session, camp);

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken)
    {
        LlmSettings settings = Require(store.Read(), needModel: false);
        InvalidOperationException? openaiError = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, settings.ModelsUrl);
            Authorize(request, settings);
            string body = await SendAsync(request, cancellationToken);
            IReadOnlyList<string> names = ParseModelIds(body);
            if (names.Count > 0)
                return names;
        }
        catch (InvalidOperationException ex)
        {
            openaiError = ex;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, settings.OllamaTagsUrl);
            Authorize(request, settings);
            string body = await SendAsync(request, cancellationToken);
            IReadOnlyList<string> names = ParseModelIds(body);
            if (names.Count > 0)
                return names;
        }
        catch (InvalidOperationException ex)
        {
            throw openaiError ?? ex;
        }

        return [];
    }

    public async Task<AgentReply> AskAsync(string? message, IReadOnlyList<AgentHistoryTurn>? history, CancellationToken cancellationToken)
    {
        string question = (message ?? "").Trim();
        if (question.Length == 0)
            throw new ArgumentException("Ask a question first.");
        if (question.Length > 4000)
            throw new ArgumentException("That question is too long.");

        LlmSettings settings = Require(store.Read(), needModel: true);
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = SystemPrompt }
        };
        if (history is not null)
        {
            foreach (AgentHistoryTurn turn in history.TakeLast(8))
            {
                if (turn.Role is not ("user" or "assistant") || string.IsNullOrWhiteSpace(turn.Content))
                    continue;
                string content = turn.Content.Trim();
                if (content.Length > 4000)
                    content = content[..4000];
                messages.Add(new JsonObject { ["role"] = turn.Role, ["content"] = content });
            }
        }

        messages.Add(new JsonObject { ["role"] = "user", ["content"] = question });
        var actions = new List<AgentAction>();
        ScanReport? scan = null;
        AntennaAdvice? antenna = null;
        string? listenText = null;
        bool allowTools = true;

        for (int round = 0; round < MaxRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ChatReply reply = await CompleteAsync(settings, messages, allowTools, cancellationToken);
            allowTools = reply.ToolsAccepted;
            if (reply.Calls.Count == 0 || actions.Count >= 8)
            {
                string answer = string.IsNullOrWhiteSpace(reply.Content)
                    ? "The model returned no words."
                    : reply.Content.Trim();
                return new AgentReply(answer, actions, scan, antenna, listenText);
            }

            if (reply.NativeTools)
                messages.Add(reply.Assistant.DeepClone());
            else
                messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = reply.Content });

            foreach (ToolCall call in reply.Calls)
            {
                if (actions.Count >= 8)
                    break;
                cancellationToken.ThrowIfCancellationRequested();
                ToolOutcome outcome = await _tools.RunAsync(call.Name, call.Arguments, cancellationToken);
                actions.Add(new AgentAction(call.Name, call.Arguments, outcome.Summary));
                scan = outcome.Scan ?? scan;
                antenna = outcome.Antenna ?? antenna;
                listenText = outcome.ListenText ?? listenText;
                string payload = outcome.Json.Length > 8000 ? outcome.Json[..8000] : outcome.Json;
                if (reply.NativeTools)
                {
                    messages.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = call.Id,
                        ["content"] = payload
                    });
                }
                else
                {
                    messages.Add(new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = $"Result of {call.Name}: {payload}"
                    });
                }
            }
        }

        return new AgentReply("The model was still asking for tools, so this turn stopped.", actions, scan, antenna, listenText);
    }

    private async Task<ChatReply> CompleteAsync(LlmSettings settings, JsonArray messages, bool includeTools, CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["model"] = settings.Model,
            ["temperature"] = 0.2,
            ["max_tokens"] = 800,
            ["messages"] = messages.DeepClone()
        };
        if (includeTools)
        {
            payload["tools"] = AgentTools.Schema.DeepClone();
            payload["tool_choice"] = "auto";
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, settings.ChatUrl);
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        Authorize(request, settings);
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException("The model endpoint did not answer. Check the base URL.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("The model endpoint took too long to answer.");
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("The endpoint refused the API key.");
        if (!response.IsSuccessStatusCode)
        {
            if (includeTools && (int)response.StatusCode == 400 && body.Contains("tool", StringComparison.OrdinalIgnoreCase))
            {
                ChatReply plain = await CompleteAsync(settings, messages, includeTools: false, cancellationToken);
                return plain with { ToolsAccepted = false };
            }

            throw new InvalidOperationException($"The model endpoint returned {(int)response.StatusCode}.");
        }

        return ParseReply(body, includeTools);
    }

    private static ChatReply ParseReply(string body, bool toolsAccepted)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(body) as JsonObject ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("The model endpoint did not return chat JSON.");
        }

        JsonObject? message = root["choices"]?[0]?["message"] as JsonObject;
        if (message is null)
            throw new InvalidOperationException("The model returned no answer.");
        string? content = ReadString(message["content"]);
        var calls = new List<ToolCall>();
        bool native = false;
        if (message["tool_calls"] is JsonArray toolCalls)
        {
            foreach (JsonNode? node in toolCalls)
            {
                if (node is not JsonObject call)
                    continue;
                string? name = ReadString(call["function"]?["name"]);
                if (string.IsNullOrWhiteSpace(name) || !AgentTools.Known(name))
                    continue;
                calls.Add(new ToolCall(
                    ReadString(call["id"]) ?? "call",
                    name,
                    ArgumentsText(call["function"]?["arguments"])));
                native = true;
            }
        }

        if (!native && TryTextTool(content, out ToolCall textCall))
            calls.Add(textCall);
        return new ChatReply(content, calls, message, native, toolsAccepted);
    }

    private static bool TryTextTool(string? content, out ToolCall call)
    {
        call = new ToolCall("", "", "{}");
        if (string.IsNullOrWhiteSpace(content))
            return false;
        string text = content.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            int first = text.IndexOf('\n');
            int last = text.LastIndexOf("```", StringComparison.Ordinal);
            if (first < 0 || last <= first)
                return false;
            text = text[(first + 1)..last].Trim();
        }

        JsonObject? obj;
        try
        {
            obj = JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            return false;
        }

        if (obj is null)
            return false;
        string? name = ReadString(obj["tool"]) ?? ReadString(obj["name"]);
        if (string.IsNullOrWhiteSpace(name) || !AgentTools.Known(name))
            return false;
        call = new ToolCall("text", name, ArgumentsText(obj["arguments"]));
        return true;
    }

    private static string? ReadString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static string ArgumentsText(JsonNode? node)
    {
        if (node is null)
            return "{}";
        if (node is JsonValue value && value.TryGetValue(out string? text))
            return string.IsNullOrWhiteSpace(text) ? "{}" : text;
        return node.ToJsonString();
    }

    private async Task<string> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                throw new InvalidOperationException("The endpoint refused the API key.");
            if (!response.IsSuccessStatusCode)
            {
                string detail = body.Trim();
                if (detail.Length > 160)
                    detail = detail[..160].Trim() + "…";
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(detail)
                        ? $"The model endpoint returned {(int)response.StatusCode}."
                        : $"The model endpoint returned {(int)response.StatusCode}: {detail}");
            }
            return body;
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException(
                $"The model endpoint did not answer ({request.RequestUri}). Check the address and that this computer can reach it.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("The model endpoint took too long to answer.");
        }
    }

    private static IReadOnlyList<string> ParseModelIds(string body)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("The model endpoint did not return a model list.");
        }

        JsonArray? data = root as JsonArray
            ?? root?["data"] as JsonArray
            ?? root?["models"] as JsonArray;
        if (data is null)
            return [];
        var names = new List<string>();
        foreach (JsonNode? item in data)
        {
            string? id = item?["id"]?.GetValue<string>()
                ?? item?["name"]?.GetValue<string>()
                ?? item?["model"]?.GetValue<string>()
                ?? item?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(id) && !names.Contains(id, StringComparer.Ordinal))
                names.Add(id);
        }

        return names;
    }

    private static LlmSettings Require(LlmSettings settings, bool needModel)
    {
        if (string.IsNullOrWhiteSpace(settings.BaseUrl))
            throw new InvalidOperationException("Save an endpoint first. For Ollama that is http://host:11434 or …/v1.");
        if (needModel && string.IsNullOrWhiteSpace(settings.Model))
            throw new InvalidOperationException("Type the model name the endpoint expects, or list models and pick one.");
        return settings;
    }

    private static void Authorize(HttpRequestMessage request, LlmSettings settings)
    {
        if (!string.IsNullOrEmpty(settings.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
    }

    private const string SystemPrompt =
        """
        You operate a receive-only software-defined radio for a beginner. Use the tools to look at the radio. Do not invent frequencies, callsigns, or signals. Frequencies for listen and inspect are in hertz. Band ids include 2m, 70cm, 40m, fm-bc, am-bc, noaa, cb, gmrs, and dmr. Prefer a band the antenna recommends. DMR voice uses a proprietary codec and is not decoded. Explore mode scans and listens with the audio on. Camp mode tunes a frequency and keeps the speaker quiet until that channel is active. camp_now camps right away; a schedule is optional. Times are local HH:mm. Leave start and end empty for a watch that runs only when nothing else is scheduled, and allow only one of those. New entries that overlap are rejected. set_schedule_policy, follow_schedule, and ignore_schedule choose whether a timed entry may take the radio from an explicit camp. Explain the result in plain language. If you cannot call tools directly, reply with only a JSON object of the form {"tool":"scan_band","arguments":{"bandId":"2m"}} and nothing else.
        """;

    private sealed record ToolCall(string Id, string Name, string Arguments);

    private sealed record ChatReply(string? Content, IReadOnlyList<ToolCall> Calls, JsonObject Assistant, bool NativeTools, bool ToolsAccepted);
}
