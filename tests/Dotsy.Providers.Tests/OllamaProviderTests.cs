using System.Net;
using System.Text;
using Dotsy.Core.Config;
using Dotsy.Core.Loop;
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
    public async Task AgentLoop_RawToolCallInOllamaContent_ExecutesToolInsteadOfPrintingInvocation()
    {
        const string todoTurn = """
            {"message":{"content":"I'll make a todo.\n<function=Todo>\n<parameter=items>\n[\"find unfinished todo section\"]\n</parameter>\n</function>\n</tool_call>"},"done":false}
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

        var ctx = new LoopContext
        {
            TokenBudget = new TokenBudget(200_000, 16_384, 20_000, 0)
        };
        ctx.Messages.Add(new UserMessage([new TextBlock("find unfinished todo items")]));

        var loop = new AgentLoop(provider, registry, YoloStore(), MakeConfig());
        var events = await CollectLoop(loop.RunAsync(ctx, Environment.CurrentDirectory, CancellationToken.None));

        var visibleText = string.Concat(events.OfType<TextChunk>().Select(t => t.Text));
        Assert.IsFalse(visibleText.Contains("<tool_call>", StringComparison.Ordinal));
        Assert.IsFalse(visibleText.Contains("<function=Todo>", StringComparison.Ordinal));

        var todoFinished = events.OfType<ToolFinished>().FirstOrDefault(tf => tf.Name == "Todo");
        Assert.IsNotNull(todoFinished, "The raw Ollama tool invocation should execute the Todo tool.");
        Assert.IsFalse(todoFinished.Result.IsError, todoFinished.Result.Content);
        CollectionAssert.AreEqual(
            new[] { "find unfinished todo section" },
            ctx.TodoItems.ToArray());

        var ended = events.OfType<LoopEnded>().LastOrDefault();
        Assert.IsNotNull(ended);
        Assert.AreEqual(EndReason.TaskComplete, ended.Reason);
    }

    private static DotsyConfig MakeConfig() =>
        new()
        {
            Model = new ModelConfig { Id = "qwen3-coder", MaxOutputTokensPerRequest = 1024 },
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
            var body = _bodies.Count > 0 ? _bodies.Dequeue() : "";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/x-ndjson")
            });
        }
    }
}
