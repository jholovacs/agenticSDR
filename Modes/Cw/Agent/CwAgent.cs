using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticSdr.Privilege;
using AgenticSdr.Shared;

namespace AgenticSdr.Cw.Agent;

public sealed record CwAgentAction(string Tool, string Arguments, string Result);

public sealed record CwAgentReply(string Answer, IReadOnlyList<CwAgentAction> Actions);

public sealed record CwAgentHistoryTurn(string? Role, string? Content);

/// <summary>
/// CW-desk instruct agent. Shares the same LLM settings as Host. Tools cover chat status and CW jargon translation for RX/TX.
/// </summary>
public sealed class CwAgent(
    LlmStore store,
    CwChat chat,
    OperatorSession operators,
    CwRadioHolder radio,
    HttpClient http)
{
    const int MaxRounds = 8;
    const int MaxActions = 6;

    static readonly JsonArray ToolSchema = JsonNode.Parse(
        """
        [
          {"type":"function","function":{"name":"get_chat","description":"Recent CW chat messages (rx, tx, notes). Use before translating what was heard or sent.","parameters":{"type":"object","properties":{"limit":{"type":"integer"}}}}},
          {"type":"function","function":{"name":"get_operator","description":"Signed-in control operator call sign, license, and grid.","parameters":{"type":"object","properties":{}}}},
          {"type":"function","function":{"name":"get_radio","description":"Whether a radio is open, listen frequency, and transmit/simulate status.","parameters":{"type":"object","properties":{}}}},
          {"type":"function","function":{"name":"translate_cw","description":"Translate CW jargon (Q-codes, prosigns, abbreviations) in plain language. direction is rx for received copy or tx for an outbound draft.","parameters":{"type":"object","properties":{"text":{"type":"string"},"direction":{"type":"string","description":"rx or tx"}},"required":["text"]}}}
        ]
        """)!.AsArray();

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
            return ParseModelIds(body);
        }
        catch (InvalidOperationException ex)
        {
            throw openaiError ?? ex;
        }
    }

    public async Task<CwAgentReply> AskAsync(string? message, IReadOnlyList<CwAgentHistoryTurn>? history, CancellationToken cancellationToken)
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
            foreach (CwAgentHistoryTurn turn in history.TakeLast(8))
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
        var actions = new List<CwAgentAction>();
        bool allowTools = true;

        for (int round = 0; round < MaxRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ChatReply reply = await CompleteAsync(settings, messages, allowTools, cancellationToken);
            allowTools = reply.ToolsAccepted;
            if (reply.Calls.Count == 0)
            {
                string answer = string.IsNullOrWhiteSpace(reply.Content)
                    ? "I could not form an answer. Try asking me to translate a specific CW line."
                    : reply.Content.Trim();
                return new CwAgentReply(answer, actions);
            }

            if (reply.NativeTools)
                messages.Add(reply.Assistant.DeepClone());
            else
                messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = reply.Content });

            foreach (ToolCall call in reply.Calls)
            {
                if (actions.Count >= MaxActions)
                    break;
                cancellationToken.ThrowIfCancellationRequested();
                (string summary, string json) = RunTool(call.Name, call.Arguments);
                actions.Add(new CwAgentAction(call.Name, call.Arguments, summary));
                string payload = json.Length > 8000 ? json[..8000] : json;
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
                        ["content"] = $"Tool {call.Name} result:\n{payload}"
                    });
                }
            }
        }

        return new CwAgentReply("I hit the tool limit for this turn. Ask again with a narrower question.", actions);
    }

    (string Summary, string Json) RunTool(string name, string argumentsJson)
    {
        JsonObject args;
        try
        {
            args = string.IsNullOrWhiteSpace(argumentsJson)
                ? new JsonObject()
                : JsonNode.Parse(argumentsJson) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            args = new JsonObject();
        }

        switch (name)
        {
            case "get_chat":
            {
                int limit = args["limit"]?.GetValue<int?>() is int n and > 0 ? Math.Min(n, 40) : 20;
                var messages = chat.Snapshot().TakeLast(limit).ToArray();
                return ("Read the CW chat.", JsonSerializer.Serialize(new { messages }));
            }
            case "get_operator":
            {
                OperatorIdentity? op = operators.Current;
                return ("Read the operator.", JsonSerializer.Serialize(new
                {
                    signedIn = op is not null,
                    callSign = op?.CallSign,
                    license = op?.License.ToString(),
                    grid = op?.Grid
                }));
            }
            case "get_radio":
                return ("Read the radio.", JsonSerializer.Serialize(radio.Status()));
            case "translate_cw":
            {
                string text = args["text"]?.GetValue<string>() ?? "";
                string direction = args["direction"]?.GetValue<string>() ?? "rx";
                object explained = CwJargon.Explain(text, direction);
                return ("Translated CW jargon.", JsonSerializer.Serialize(explained));
            }
            default:
                return ($"Unknown tool {name}.", JsonSerializer.Serialize(new { error = $"Unknown tool {name}." }));
        }
    }

    async Task<ChatReply> CompleteAsync(LlmSettings settings, JsonArray messages, bool includeTools, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["model"] = settings.Model,
            ["messages"] = messages.DeepClone(),
            ["temperature"] = 0.2
        };
        if (includeTools)
        {
            body["tools"] = ToolSchema.DeepClone();
            body["tool_choice"] = "auto";
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, settings.ChatUrl)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        Authorize(request, settings);
        string raw = await SendAsync(request, cancellationToken);
        JsonNode root = JsonNode.Parse(raw)
            ?? throw new InvalidOperationException("The model endpoint returned empty JSON.");
        JsonObject? choice = root["choices"]?[0]?["message"] as JsonObject;
        if (choice is null)
            throw new InvalidOperationException("The model endpoint did not return a message.");

        string content = choice["content"]?.GetValue<string>() ?? "";
        var calls = new List<ToolCall>();
        if (choice["tool_calls"] is JsonArray native)
        {
            foreach (JsonNode? item in native)
            {
                string id = item?["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("n");
                string name = item?["function"]?["name"]?.GetValue<string>() ?? "";
                string args = item?["function"]?["arguments"]?.GetValue<string>() ?? "{}";
                if (!string.IsNullOrWhiteSpace(name))
                    calls.Add(new ToolCall(id, name, args));
            }

            if (calls.Count > 0)
                return new ChatReply(content, calls, choice, NativeTools: true, ToolsAccepted: true);
        }

        if (TryParsePlainTool(content, out ToolCall? plain) && plain is not null)
            return new ChatReply(content, [plain], choice, NativeTools: false, ToolsAccepted: includeTools);

        return new ChatReply(content, calls, choice, NativeTools: false, ToolsAccepted: includeTools);
    }

    static bool TryParsePlainTool(string content, out ToolCall? call)
    {
        call = null;
        string trimmed = content.Trim();
        if (!trimmed.StartsWith('{') || !trimmed.Contains("\"tool\"", StringComparison.Ordinal))
            return false;
        try
        {
            JsonNode? node = JsonNode.Parse(trimmed);
            string? name = node?["tool"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name))
                return false;
            string args = node?["arguments"]?.ToJsonString() ?? "{}";
            call = new ToolCall(Guid.NewGuid().ToString("n"), name, args);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    async Task<string> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                string detail = body.Length > 240 ? body[..240] : body;
                throw new InvalidOperationException(
                    $"The model endpoint returned {(int)response.StatusCode}. {detail}");
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

    static IReadOnlyList<string> ParseModelIds(string body)
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

    static LlmSettings Require(LlmSettings settings, bool needModel)
    {
        if (string.IsNullOrWhiteSpace(settings.BaseUrl))
            throw new InvalidOperationException("Save an endpoint first. For Ollama that is http://host:11434 or …/v1.");
        if (needModel && string.IsNullOrWhiteSpace(settings.Model))
            throw new InvalidOperationException("Type the model name the endpoint expects, or list models and pick one.");
        return settings;
    }

    static void Authorize(HttpRequestMessage request, LlmSettings settings)
    {
        if (!string.IsNullOrEmpty(settings.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
    }

    const string SystemPrompt =
        """
        You help a licensed amateur radio operator on the CW (Morse) desk. You do not transmit and you cannot forge the Transmit click.
        Translate CW jargon for received (rx) or outbound (tx) traffic: Q-codes, prosigns, and common abbreviations.
        When asked what something means, call get_chat if needed, then translate_cw with direction rx or tx, and explain in plain language.
        Do not invent call signs or traffic that is not in the chat or the user's message.
        Always finish with a short plain-language answer. If you cannot call tools, reply with only {"tool":"translate_cw","arguments":{"text":"CQ DE W1AW K","direction":"rx"}}.
        """;

    sealed record ToolCall(string Id, string Name, string Arguments);

    sealed record ChatReply(string? Content, IReadOnlyList<ToolCall> Calls, JsonObject Assistant, bool NativeTools, bool ToolsAccepted);
}
