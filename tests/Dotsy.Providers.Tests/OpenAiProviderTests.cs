using Dotsy.Core.Providers;
using Dotsy.Providers.OpenAi;
using Dotsy.Providers.Tests.Helpers;

namespace Dotsy.Providers.Tests;

[TestClass]
public sealed class OpenAiProviderTests
{
    private static OpenAiProvider Provider(string sseBody)
    {
        var http = new HttpClient(new FakeSseHandler(sseBody));
        return new OpenAiProvider("test-key", "https://api.openai.com", http);
    }

    private static ChatRequest MinimalRequest() =>
        new("gpt-4o", "sys", [new UserMessage([new TextBlock("hi")])], [], 1024);

    private static async Task<List<ProviderEvent>> Collect(IAsyncEnumerable<ProviderEvent> src)
    {
        var list = new List<ProviderEvent>();
        await foreach (var ev in src)
            list.Add(ev);
        return list;
    }

    // ── Delta accumulation ────────────────────────────────────────────────────

    [TestMethod]
    public async Task ParseSse_TextDelta_AccumulatesFragments()
    {
        const string sse = """
            data: {"choices":[{"delta":{"content":"Hello "},"index":0,"finish_reason":null}]}

            data: {"choices":[{"delta":{"content":"world"},"index":0,"finish_reason":null}]}

            data: {"choices":[{"delta":{},"index":0,"finish_reason":"stop"}]}

            data: [DONE]

            """;

        var events = await Collect(Provider(sse).StreamAsync(MinimalRequest(), CancellationToken.None));

        var texts = events.OfType<TextDelta>().Select(t => t.Text).ToList();
        Assert.AreEqual(2, texts.Count);
        Assert.AreEqual("Hello ", texts[0]);
        Assert.AreEqual("world",  texts[1]);
    }

    // ── Finish reason mapping ─────────────────────────────────────────────────

    [TestMethod]
    public async Task ParseSse_FinishReason_Stop_MapsToEndTurn()
    {
        const string sse = """
            data: {"choices":[{"delta":{},"index":0,"finish_reason":"stop"}]}

            data: [DONE]

            """;

        var events = await Collect(Provider(sse).StreamAsync(MinimalRequest(), CancellationToken.None));

        var end = events.OfType<StreamEnd>().Single();
        Assert.AreEqual(StopReason.EndTurn, end.Reason);
    }

    [TestMethod]
    public async Task ParseSse_FinishReason_ToolCalls_MapsToToolUse()
    {
        const string sse = """
            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","function":{"name":"Read","arguments":"{\"path\":\"f.cs\"}"}}]},"index":0,"finish_reason":null}]}

            data: {"choices":[{"delta":{},"index":0,"finish_reason":"tool_calls"}]}

            data: [DONE]

            """;

        var events = await Collect(Provider(sse).StreamAsync(MinimalRequest(), CancellationToken.None));

        var end = events.OfType<StreamEnd>().Single();
        Assert.AreEqual(StopReason.ToolUse, end.Reason);

        var tc = events.OfType<ToolCallDelta>().Single();
        Assert.AreEqual("call_1", tc.Id);
        Assert.AreEqual("Read",   tc.Name);
        Assert.AreEqual("{\"path\":\"f.cs\"}", tc.ArgumentsJson);
    }

    // ── Usage chunk ───────────────────────────────────────────────────────────

    [TestMethod]
    public async Task ParseSse_UsageChunk_EmitsUsageUpdate()
    {
        const string sse = """
            data: {"choices":[{"delta":{},"index":0,"finish_reason":"stop"}]}

            data: {"usage":{"prompt_tokens":20,"completion_tokens":8}}

            data: [DONE]

            """;

        var events = await Collect(Provider(sse).StreamAsync(MinimalRequest(), CancellationToken.None));

        var usage = events.OfType<UsageUpdate>().Single();
        Assert.AreEqual(20, usage.InputTokens);
        Assert.AreEqual(8,  usage.OutputTokens);
    }

    // ── Tool call argument accumulation ───────────────────────────────────────

    [TestMethod]
    public async Task ParseSse_ToolCallArguments_AccumulatedAcrossChunks()
    {
        const string sse = """
            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_x","function":{"name":"Edit","arguments":"{\"path\":"}}]},"index":0,"finish_reason":null}]}

            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"main.cs\"}"}}]},"index":0,"finish_reason":null}]}

            data: {"choices":[{"delta":{},"index":0,"finish_reason":"tool_calls"}]}

            data: [DONE]

            """;

        var events = await Collect(Provider(sse).StreamAsync(MinimalRequest(), CancellationToken.None));

        var tc = events.OfType<ToolCallDelta>().Single();
        Assert.AreEqual("{\"path\":\"main.cs\"}", tc.ArgumentsJson);
    }
}
