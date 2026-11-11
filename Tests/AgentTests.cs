using System.Net;
using System.Text;
using AgenticSdr.Host;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgenticSdr.Tests;

/// <summary>Checks that an OpenAI chat response can request a radio tool, and that the API key is not echoed.</summary>
public class AgentTests
{
    [Fact]
    public void Speech_audio_is_resampled_to_16_kilohertz()
    {
        var audio = new float[48_000];
        for (int i = 0; i < audio.Length; i++)
            audio[i] = MathF.Sin(2 * MathF.PI * 1_000 * i / 48_000);
        float[] narrow = SpeechDecoder.Resample(audio, 48_000, SpeechDecoder.ModelRate);
        Assert.Equal(16_000, narrow.Length);
        Assert.InRange(narrow[0], -0.01f, 0.01f);
    }

    [Fact]
    public void A_bare_ollama_host_is_normalized_to_the_v1_root()
    {
        Assert.Equal("http://10.10.26.10:11434/v1", LlmStore.NormalizeBase("http://10.10.26.10:11434"));
        Assert.Equal("http://10.10.26.10:11434/v1", LlmStore.NormalizeBase("http://10.10.26.10:11434/"));
        Assert.Equal("http://10.10.26.10:11434/v1", LlmStore.NormalizeBase("http://10.10.26.10:11434/v1"));
        Assert.Equal("http://10.10.26.10:11434/v1", LlmStore.NormalizeBase("http://10.10.26.10:11434/api/tags"));
        Assert.Equal(
            "http://10.10.26.10:11434/api/tags",
            new LlmSettings("http://10.10.26.10:11434/v1", "", "").OllamaTagsUrl);
    }

    [Fact]
    public void Saved_key_is_not_returned_to_the_page()
    {
        string path = Path.Combine(Path.GetTempPath(), "agentic-sdr-llm-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new LlmStore(path);
            LlmPublic saved = store.Save(new LlmSave("https://api.openai.com/v1/chat/completions", "gpt-test", "sk-secret-value", false));
            Assert.Equal("https://api.openai.com/v1", saved.BaseUrl);
            Assert.True(saved.HasKey);
            Assert.Equal("alue", saved.KeyHint);
            Assert.DoesNotContain("sk-secret-value", System.Text.Json.JsonSerializer.Serialize(saved));

            LlmPublic kept = store.Save(new LlmSave("https://api.openai.com/v1", "gpt-test", "", false));
            Assert.True(kept.HasKey);
            Assert.Equal("sk-secret-value", store.Read().ApiKey);

            LlmPublic cleared = store.Save(new LlmSave("https://api.openai.com/v1", "gpt-test", "", true));
            Assert.False(cleared.HasKey);
            Assert.Equal("", store.Read().ApiKey);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task Listing_models_accepts_ollama_tags_when_openai_is_empty()
    {
        var script = new GetScriptHandler(
            ("/v1/models", """{"object":"list","data":[]}"""),
            ("/api/tags", """{"models":[{"name":"llama3.2:latest"},{"name":"qwen2.5:7b"}]}"""));
        InstructAgent agent = Agent(script, "");
        IReadOnlyList<string> names = await agent.ListModelsAsync(CancellationToken.None);
        Assert.Equal(["llama3.2:latest", "qwen2.5:7b"], names);
    }

    [Fact]
    public async Task A_tool_call_is_run_and_the_key_stays_out_of_the_answer()
    {
        var script = new ScriptHandler(
            """{"choices":[{"message":{"role":"assistant","content":null,"tool_calls":[{"id":"call_1","type":"function","function":{"name":"list_ham_bands","arguments":"{}"}}]}}]}""",
            """{"choices":[{"message":{"role":"assistant","content":"Two meters is a good place to start."}}]}""");
        InstructAgent agent = Agent(script, "sk-secret-value");
        AgentReply reply = await agent.AskAsync("Which band should I scan?", null, CancellationToken.None);

        Assert.Equal("Two meters is a good place to start.", reply.Answer);
        Assert.Equal("list_ham_bands", reply.Actions[0].Tool);
        Assert.Contains("2m", script.Bodies[1]);
        Assert.Equal("Bearer sk-secret-value", script.Authorization);
        Assert.DoesNotContain("sk-secret-value", reply.Answer);
    }

    [Fact]
    public async Task A_plain_instruct_reply_can_name_a_tool()
    {
        var script = new ScriptHandler(
            """{"choices":[{"message":{"role":"assistant","content":"{\"tool\":\"list_ham_bands\",\"arguments\":{}}"}}]}""",
            """{"choices":[{"message":{"role":"assistant","content":"There are several bands."}}]}""");
        InstructAgent agent = Agent(script, "");
        AgentReply reply = await agent.AskAsync("List the bands.", null, CancellationToken.None);

        Assert.Equal("There are several bands.", reply.Answer);
        Assert.Equal("list_ham_bands", reply.Actions[0].Tool);
        Assert.Contains("Result of list_ham_bands", script.Bodies[1]);
        Assert.Null(script.Authorization);
    }

    [Fact]
    public async Task A_server_that_rejects_tools_is_asked_in_plain_text()
    {
        var script = new ScriptHandler(
            status: HttpStatusCode.BadRequest,
            """{"error":"tools are not supported"}""",
            """{"choices":[{"message":{"role":"assistant","content":"Tell me the band and I will scan it."}}]}""");
        InstructAgent agent = Agent(script, "");
        AgentReply reply = await agent.AskAsync("Help me listen.", null, CancellationToken.None);

        Assert.Equal("Tell me the band and I will scan it.", reply.Answer);
        Assert.Contains("\"tools\"", script.Bodies[0]);
        Assert.DoesNotContain("\"tools\"", script.Bodies[1]);
    }

    private static InstructAgent Agent(HttpMessageHandler script, string apiKey)
    {
        string path = Path.Combine(Path.GetTempPath(), "agentic-sdr-llm-" + Guid.NewGuid().ToString("N") + ".json");
        var store = new LlmStore(path);
        store.Save(new LlmSave("http://127.0.0.1:9/v1", "test-instruct", apiKey, false));
        var http = new HttpClient(script) { Timeout = TimeSpan.FromSeconds(5) };
        var session = new RadioSession(
            new SdrOptions(),
            new ClipStore(),
            new RepeaterLookup(new HttpClient(), NullLogger<RepeaterLookup>.Instance),
            new CallsignLookup(
                new HttpClient(),
                NullLogger<CallsignLookup>.Instance,
                Path.Combine(Path.GetTempPath(), "agentic-sdr-calls-" + Guid.NewGuid().ToString("N") + ".json"),
                enabled: false),
            new SpeechDecoder(Path.GetTempPath()),
            NullLogger<RadioSession>.Instance);
        var camp = new CampBook(path + ".schedule");
        var clips = new ClipStore();
        return new InstructAgent(store, session, camp, clips, http);
    }

    [Fact]
    public void Agent_tools_include_digi_clip_analysis()
    {
        Assert.True(AgentTools.Known("analyze_clip"));
        Assert.True(AgentTools.Known("list_clips"));
        string schema = AgentTools.Schema.ToJsonString();
        Assert.Contains("analyze_clip", schema, StringComparison.Ordinal);
        Assert.Contains("list_clips", schema, StringComparison.Ordinal);
    }

    private sealed class ScriptHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _responses = new();
        public List<string> Bodies { get; } = [];
        public string? Authorization { get; private set; }

        public ScriptHandler(params string[] bodies)
        {
            foreach (string body in bodies)
                _responses.Enqueue((HttpStatusCode.OK, body));
        }

        public ScriptHandler(HttpStatusCode status, params string[] bodies)
        {
            _responses.Enqueue((status, bodies[0]));
            for (int i = 1; i < bodies.Length; i++)
                _responses.Enqueue((HttpStatusCode.OK, bodies[i]));
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization?.ToString();
            Bodies.Add(request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken));
            (HttpStatusCode status, string body) = _responses.Dequeue();
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class GetScriptHandler(params (string Path, string Body)[] routes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri?.AbsolutePath ?? "";
            foreach ((string expected, string body) in routes)
            {
                if (path.Equals(expected, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(body, Encoding.UTF8, "application/json")
                    });
                }
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("missing", Encoding.UTF8, "text/plain")
            });
        }
    }
}
