using Dotsy.Core.Providers;
using Dotsy.Providers.Anthropic;
using Dotsy.Providers.Tests.Helpers;

namespace Dotsy.Providers.Tests;

[TestClass]
public sealed class AnthropicProviderTests
{
    private static AnthropicProvider Provider(string sseBody)
    {
        var http = new HttpClient(new FakeSseHandler(sseBody));
        return new AnthropicProvider("test-key", http);
    }

    private static ChatRequest MinimalRequest() =>
        new("claude-opus-4-7", "sys", [new UserMessage([new TextBlock("hi")])], [], 1024);

    private static async Task<List<ProviderEvent>> Collect(IAsyncEnumerable<ProviderEvent> src)
    {
        var list = new List<ProviderEvent>();
        await foreach (var ev in src)
            list.Add(ev);
        return list;
    }

    // ── Text delta ────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task ParseSse_TextDelta_EmitsTextDelta()
    {
        const string sse = """
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hello "}}

            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"world"}}

            data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"input_tokens":10,"output_tokens":5}}

            """;

        var events = await Collect(Provider(sse).StreamAsync(MinimalRequest(), CancellationToken.None));

        var texts = events.OfType<TextDelta>().Select(t => t.Text).ToList();
        Assert.AreEqual(2, texts.Count);
        Assert.AreEqual("Hello ", texts[0]);
        Assert.AreEqual("world", texts[1]);
    }

    // ── Tool call accumulation ────────────────────────────────────────────────

    [TestMethod]
    public async Task ParseSse_ToolCall_AccumulatesFragmentsAndEmitsOnStop()
    {
        const string sse = """
            data: {"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_01","name":"Read"}}

            data: {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"path\":"}}

            data: {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"\"file.cs\"}"}}

            data: {"type":"content_block_stop","index":0}

            data: {"type":"message_delta","delta":{"stop_reason":"tool_use"}}

            """;

        var events = await Collect(Provider(sse).StreamAsync(MinimalRequest(), CancellationToken.None));

        var tc = events.OfType<ToolCallDelta>().Single();
        Assert.AreEqual("toolu_01", tc.Id);
        Assert.AreEqual("Read",     tc.Name);
        Assert.AreEqual("{\"path\":\"file.cs\"}", tc.ArgumentsJson);
    }

    // ── Usage update ──────────────────────────────────────────────────────────

    [TestMethod]
    public async Task ParseSse_UsageUpdate_ReturnsCorrectCounts()
    {
        const string sse = """
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"input_tokens":42,"output_tokens":17,"cache_read_input_tokens":5,"cache_creation_input_tokens":3}}

            """;

        var events = await Collect(Provider(sse).StreamAsync(MinimalRequest(), CancellationToken.None));

        var usage = events.OfType<UsageUpdate>().Single();
        Assert.AreEqual(42, usage.InputTokens);
        Assert.AreEqual(17, usage.OutputTokens);
        Assert.AreEqual(5,  usage.CacheReadTokens);
        Assert.AreEqual(3,  usage.CacheWriteTokens);
    }

    // ── Thinking block ────────────────────────────────────────────────────────

    [TestMethod]
    public async Task ParseSse_ThinkingBlock_EmitsThinkingDelta()
    {
        const string sse = """
            data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"Let me reason..."}}

            data: {"type":"message_delta","delta":{"stop_reason":"end_turn"}}

            """;

        var events = await Collect(Provider(sse).StreamAsync(MinimalRequest(), CancellationToken.None));

        var thinking = events.OfType<ThinkingDelta>().Single();
        Assert.AreEqual("Let me reason...", thinking.Text);
    }

    // ── StreamEnd stop reason ─────────────────────────────────────────────────

    [TestMethod]
    public async Task ParseSse_StopReason_MappedCorrectly()
    {
        const string sse = """
            data: {"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"input_tokens":1,"output_tokens":1}}

            """;

        var events = await Collect(Provider(sse).StreamAsync(MinimalRequest(), CancellationToken.None));

        var end = events.OfType<StreamEnd>().Single();
        Assert.AreEqual(StopReason.ToolUse, end.Reason);
    }
}
