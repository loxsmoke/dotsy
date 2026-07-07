using System.Net;
using System.Text;
using Dotsy.Core.Config;
using Dotsy.Core.Loop;
using Dotsy.Core.Loop.Data;
using Dotsy.Core.Providers;
using Dotsy.Core.Tools;
using Dotsy.Providers.Ollama;
using Dotsy.Providers.Tests.Helpers;

namespace Dotsy.Providers.Tests;

[TestClass]
public sealed class OllamaProviderTests
{
    private static OllamaProvider Provider(string body)
    {
        var http = new HttpClient(new FakeSseHandler(body));
        return new OllamaProvider("http://localhost:11434", http);
    }

    private static ChatRequest MinimalRequest() =>
        new("qwen3-coder", "sys", [new UserMessage([new TextBlock("hi")])], [], 1024);

    private static async Task<List<ProviderEvent>> Collect(IAsyncEnumerable<ProviderEvent> src)
    {
        var list = new List<ProviderEvent>();
        await foreach (var ev in src)
            list.Add(ev);
        return list;
    }

    [TestMethod]
    public async Task GetModelInfo_LoadsContextLengthDynamicallyFromApiShow()
    {
        // /api/show returns model_info with an architecture-prefixed context_length key.
        var provider = Provider("""{"model_info":{"qwen3.context_length":262144}}""");

        var info = await provider.GetModelInfoAsync("qwen3-coder", CancellationToken.None);

        Assert.AreEqual(262_144, info.ContextWindow);
        // /api/show reports the architecture ceiling, not the loaded window — flagged Advertised.
        Assert.AreEqual(ModelInfoSource.Advertised, info.Source);
    }

    [TestMethod]
    public async Task GetModelInfo_FallsBackWhenNoContextLengthReported()
    {
        var provider = Provider("""{"model_info":{}}""");

        var info = await provider.GetModelInfoAsync("qwen3-coder", CancellationToken.None);

        Assert.AreEqual(128_000, info.ContextWindow);
    }

    [TestMethod]
    public async Task GetModelInfo_ConfiguredMaxContextTokensWinsAsActiveWindow()
    {
        // With max_context_tokens set, that value is sent as num_ctx on every call, so it is the
        // active window — it wins over /api/show's 256K ceiling and is not flagged Advertised.
        var http = new HttpClient(new FakeSseHandler("""{"model_info":{"qwen3.context_length":262144}}"""));
        var provider = new OllamaProvider("http://localhost:11434", http, maxContextTokens: 32_768);

        var info = await provider.GetModelInfoAsync("qwen3-coder", CancellationToken.None);

        Assert.AreEqual(32_768, info.ContextWindow);
        Assert.AreEqual(ModelInfoSource.Api, info.Source);
    }

    [TestMethod]
    public async Task GetModelInfo_PrefersLoadedContextFromApiPs()
    {
        // /api/ps reports the live num_ctx the model is actually loaded with (8192 here), which
        // must win over the architecture maximum from /api/show. The loaded name carries a :latest
        // tag the configured id omits, exercising the tag-tolerant match.
        var provider = Provider(
            """{"models":[{"name":"qwen3-coder:latest","model":"qwen3-coder:latest","context_length":8192}]}""");

        var info = await provider.GetModelInfoAsync("qwen3-coder", CancellationToken.None);

        Assert.AreEqual(8_192, info.ContextWindow);
        Assert.AreEqual(ModelInfoSource.Api, info.Source);
    }

    [TestMethod]
    public async Task StreamAsync_SendsNumCtxFromMaxContextTokens()
    {
        var handler = new FakeSseHandler("""{"done":true}""");
        var provider = new OllamaProvider(
            "http://localhost:11434", new HttpClient(handler), maxContextTokens: 131_072);

        await Collect(provider.StreamAsync(MinimalRequest(), CancellationToken.None));

        Assert.IsNotNull(handler.LastRequestBody);
        using var doc = System.Text.Json.JsonDocument.Parse(handler.LastRequestBody!);
        var numCtx = doc.RootElement.GetProperty("options").GetProperty("num_ctx").GetInt32();
        Assert.AreEqual(131_072, numCtx);
    }

    [TestMethod]
    public async Task StreamAsync_OmitsOptionsWhenMaxContextTokensUnset()
    {
        var handler = new FakeSseHandler("""{"done":true}""");
        var provider = new OllamaProvider("http://localhost:11434", new HttpClient(handler));

        await Collect(provider.StreamAsync(MinimalRequest(), CancellationToken.None));

        using var doc = System.Text.Json.JsonDocument.Parse(handler.LastRequestBody!);
        Assert.IsFalse(doc.RootElement.TryGetProperty("options", out _));
    }

    [TestMethod]
    public async Task StreamAsync_ModelNotFoundMapsToModelUnknown()
    {
        const string body = """{"error":"model 'missing' not found, try pulling it first"}""";
        var provider = new OllamaProvider(
            "http://localhost:11434",
            new HttpClient(new FakeSseHandler(body, HttpStatusCode.NotFound)));

        var events = await Collect(provider.StreamAsync(MinimalRequest(), CancellationToken.None));

        var err = events.OfType<StreamError>().Single();
        var pex = (ProviderException)err.Ex;
        var modelError = (ModelUnknownError)pex.Error;
        Assert.AreEqual("model 'missing' not found, try pulling it first", modelError.Message);
    }

    [TestMethod]
    public async Task GetModelsAsync_LoadsInstalledModelsFromApiTags()
    {
        const string body = """
            {"models":[{"name":"qwen3-coder:latest"},{"name":"llama3.2:latest"}]}
            """;
        var provider = Provider(body);

        var models = await provider.GetModelsAsync(CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { "qwen3-coder:latest", "llama3.2:latest" },
            models.Select(m => m.Id).ToArray());
    }

    [TestMethod]
    public async Task ParseNdjson_RawToolCallInContent_EmitsToolCallAndSuppressesMarkupText()
    {
        const string body = """
            {"message":{"content":"I'll search now.\n<tool_call>\n<function=Grep>\n<parameter=pattern>\nTODO\n</parameter>\n</function>\n</tool_call>"},"done":false}
            {"done":true,"prompt_eval_count":10,"eval_count":4}

            """;

        var events = await Collect(Provider(body).StreamAsync(MinimalRequest(), CancellationToken.None));

        var text = string.Concat(events.OfType<TextDelta>().Select(t => t.Text));
        Assert.AreEqual("I'll search now.\n", text);

        var tool = events.OfType<ToolCallDelta>().Single();
        Assert.AreEqual("Grep", tool.Name);
        Assert.AreEqual("""{"pattern":"TODO"}""", tool.ArgumentsJson);
    }

    [TestMethod]
    public async Task ParseNdjson_RawToolCallSplitAcrossChunks_WaitsForCompleteBlock()
    {
        const string body = """
            {"message":{"content":"Checking.\n<tool_"},"done":false}
            {"message":{"content":"call>\n<function=Read>\n<parameter=path>\ntodo.md\n</parameter>\n</function>\n</tool_call>"},"done":false}
            {"done":true}

            """;

        var events = await Collect(Provider(body).StreamAsync(MinimalRequest(), CancellationToken.None));

        var text = string.Concat(events.OfType<TextDelta>().Select(t => t.Text));
        Assert.AreEqual("Checking.\n", text);

        var tool = events.OfType<ToolCallDelta>().Single();
        Assert.AreEqual("Read", tool.Name);
        Assert.AreEqual("""{"path":"todo.md"}""", tool.ArgumentsJson);
    }

    [TestMethod]
    public async Task ParseNdjson_RawToolCallParameterWithJsonValue_PreservesJson()
    {
        const string body = """
            {"message":{"content":"<tool_call>\n<function=Todo>\n<parameter=items>\n[\"one\",\"two\"]\n</parameter>\n</function>\n</tool_call>"},"done":false}
            {"done":true}

            """;

        var events = await Collect(Provider(body).StreamAsync(MinimalRequest(), CancellationToken.None));

        var tool = events.OfType<ToolCallDelta>().Single();
        Assert.AreEqual("Todo", tool.Name);
        Assert.AreEqual("""{"items":["one","two"]}""", tool.ArgumentsJson);
    }

    [TestMethod]
    public async Task ParseNdjson_RawToolCallWithoutOpeningToolCallTag_StillEmitsToolCall()
    {
        const string body = """
            {"message":{"content":"I'll check.\n<function=Todo>\n</function>\n</tool_call>"},"done":false}
            {"done":true}

            """;

        var events = await Collect(Provider(body).StreamAsync(MinimalRequest(), CancellationToken.None));

        var text = string.Concat(events.OfType<TextDelta>().Select(t => t.Text));
        Assert.AreEqual("I'll check.\n", text);

        var tool = events.OfType<ToolCallDelta>().Single();
        Assert.AreEqual("Todo", tool.Name);
        Assert.AreEqual("""{}""", tool.ArgumentsJson);
    }

    [TestMethod]
    public async Task ParseNdjson_NativeThinkingField_EmitsThinkingDelta()
    {
        const string body = """
            {"message":{"thinking":"weighing options","content":""},"done":false}
            {"message":{"content":"the answer"},"done":false}
            {"done":true}

            """;

        var events = await Collect(Provider(body).StreamAsync(MinimalRequest(), CancellationToken.None));

        Assert.AreEqual("weighing options",
            string.Concat(events.OfType<ThinkingDelta>().Select(t => t.Text)));
        Assert.AreEqual("the answer",
            string.Concat(events.OfType<TextDelta>().Select(t => t.Text)));
    }

    [TestMethod]
    public async Task ParseNdjson_InlineThinkTagInContent_RoutedToThinkingDelta()
    {
        const string body = """
            {"message":{"content":"<think>reason here</think>visible answer"},"done":false}
            {"done":true}

            """;

        var events = await Collect(Provider(body).StreamAsync(MinimalRequest(), CancellationToken.None));

        Assert.AreEqual("reason here",
            string.Concat(events.OfType<ThinkingDelta>().Select(t => t.Text)));
        Assert.AreEqual("visible answer",
            string.Concat(events.OfType<TextDelta>().Select(t => t.Text)));
    }

    [TestMethod]
    public async Task ParseNdjson_InlineThinkTagSplitAcrossChunks_StillRoutedToThinking()
    {
        // Opening tag straddles two stream chunks; the partial "<thi" must be held back.
        const string body = """
            {"message":{"content":"<thi"},"done":false}
            {"message":{"content":"nk>hidden</think>shown"},"done":false}
            {"done":true}

            """;

        var events = await Collect(Provider(body).StreamAsync(MinimalRequest(), CancellationToken.None));

        Assert.AreEqual("hidden",
            string.Concat(events.OfType<ThinkingDelta>().Select(t => t.Text)));
        Assert.AreEqual("shown",
            string.Concat(events.OfType<TextDelta>().Select(t => t.Text)));
    }

    [TestMethod]
    public async Task AgentLoop_RawToolCallInOllamaContent_ExecutesToolInsteadOfPrintingInvocation()
    {
        const string todoTurn = """
            {"message":{"content":"I'll check the todo.\n<function=Todo>\n<parameter=action>\nlist_sections\n</parameter>\n</function>\n</tool_call>"},"done":false}
            {"done":true,"prompt_eval_count":10,"eval_count":4}

            """;
        const string doneTurn = """
            {"message":{"content":"<tool_call>\n<function=Done>\n<parameter=summary>\nfinished\n</parameter>\n</function>\n</tool_call>"},"done":false}
            {"done":true,"prompt_eval_count":6,"eval_count":3}

            """;

        var provider = new OllamaProvider(
            "http://localhost:11434",
            new HttpClient(new QueueHandler(todoTurn, doneTurn)));
        var registry = new ToolRegistry();
        registry.Register(new TodoTool());
        registry.Register(new DoneTool());

        // The Todo tool reads todo.md from the working directory, so run against a temp repo.
        var cwd = Path.Combine(Path.GetTempPath(), "dotsy-ollama-todo-test", Guid.NewGuid().ToString());
        Directory.CreateDirectory(cwd);
        File.WriteAllText(Path.Combine(cwd, TodoTool.FileName),
            "# Todo\n\n## 1. Improvements\n- [ ] find unfinished todo section\n");

        try
        {
            var ctx = new LoopContext
            {
                TokenBudget = new TokenBudget(200_000, 16_384, 20_000, 0, 0.9f)
            };
            ctx.Messages.Add(new UserMessage([new TextBlock("find unfinished todo items")]));

            var loop = new AgentLoop(provider, registry, YoloStore(), MakeConfig());
            var events = await CollectLoop(loop.RunAsync(ctx, cwd, CancellationToken.None));

            var visibleText = string.Concat(events.OfType<TextChunk>().Select(t => t.Text));
            Assert.IsFalse(visibleText.Contains("<tool_call>", StringComparison.Ordinal));
            Assert.IsFalse(visibleText.Contains("<function=Todo>", StringComparison.Ordinal));

            var todoFinished = events.OfType<ToolFinished>().FirstOrDefault(tf => tf.Name == "Todo");
            Assert.IsNotNull(todoFinished, "The raw Ollama tool invocation should execute the Todo tool.");
            Assert.IsFalse(todoFinished.Result.IsError, todoFinished.Result.Content);
            // list_sections should report the section parsed from todo.md, proving the tool ran with
            // the parameters extracted from the raw <function=Todo> content.
            StringAssert.Contains(todoFinished.Result.Content, "1. Improvements");

            var ended = events.OfType<LoopEnded>().LastOrDefault();
            Assert.IsNotNull(ended);
            Assert.AreEqual(EndReason.TaskComplete, ended.Reason);
        }
        finally
        {
            Directory.Delete(cwd, recursive: true);
        }
    }

    private static DotsyConfig MakeConfig() =>
        new()
        {
            Model = new ModelConfig { Provider = ProviderConfig.Ollama, Ollama = new() { Id = "qwen3-coder" }, MaxOutputTokensPerRequest = 1024 },
            Agent = new AgentConfig
            {
                MaxTurns = 10,
                NudgeLimit = 3,
                ParallelTools = true,
                AutoLint = false,
                AutoTest = false,
                MaxReflections = 0,
                InjectEnvironment = false,
                InjectGitStatus = false
            },
            Compaction = new CompactionConfig { Enabled = false },
            Retrieval = new RetrievalConfig { RepoMapTokens = 0 },
            Skills = new SkillsConfig { Paths = [] },
            Git = new GitConfig(),
            Tui = new TuiConfig(),
            Permissions = new PermissionsConfig { AlwaysAllow = [], NeverAllow = [] },
            Session = new SessionConfig()
        };

    private static PermissionStore YoloStore() =>
        new(new PermissionsConfig { AlwaysAllow = [], NeverAllow = [] }, Path.GetTempPath())
        { Yolo = true };

    private static async Task<List<LoopEvent>> CollectLoop(IAsyncEnumerable<LoopEvent> src)
    {
        var list = new List<LoopEvent>();
        await foreach (var ev in src)
            list.Add(ev);
        return list;
    }

    private sealed class QueueHandler : HttpMessageHandler
    {
        private readonly Queue<string> _bodies;

        public QueueHandler(params string[] bodies) => _bodies = new Queue<string>(bodies);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            // The provider probes /api/show for the "thinking" capability before each chat.
            // Answer it out-of-band (qwen3-coder reports no thinking) so it doesn't consume
            // a queued chat turn.
            if (request.RequestUri?.AbsolutePath == "/api/show")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"capabilities":["completion","tools"]}""",
                        Encoding.UTF8, "application/json")
                });

            var body = _bodies.Count > 0 ? _bodies.Dequeue() : "";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/x-ndjson")
            });
        }
    }
}
