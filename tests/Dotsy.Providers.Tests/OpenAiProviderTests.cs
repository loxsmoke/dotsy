using System.Net;
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

    private static OpenAiProvider Provider(string body, HttpStatusCode status)
    {
        var http = new HttpClient(new FakeSseHandler(body, status));
        return new OpenAiProvider("test-key", "https://api.openai.com", http);
    }

    private static ChatRequest MinimalRequest() =>
        new("gpt-4o", "sys", [new UserMessage([new TextBlock("hi")])], [], 1024);

    [TestMethod]
    public async Task StreamAsync_OpenRouterBaseUrl_PreservesApiV1Path()
    {
        var handler = new FakeSseHandler("data: [DONE]\n\n");
        var provider = new OpenAiProvider("test-key", "https://openrouter.ai/api/v1", new HttpClient(handler));

        await Collect(provider.StreamAsync(MinimalRequest(), CancellationToken.None));

        Assert.AreEqual("https://openrouter.ai/api/v1/chat/completions", handler.LastRequestUri!.ToString());
    }

    [TestMethod]
    public async Task StreamAsync_OpenAiRootBaseUrl_AppendsV1Path()
    {
        var handler = new FakeSseHandler("data: [DONE]\n\n");
        var provider = new OpenAiProvider("test-key", "https://api.openai.com", new HttpClient(handler));

        await Collect(provider.StreamAsync(MinimalRequest(), CancellationToken.None));

        Assert.AreEqual("https://api.openai.com/v1/chat/completions", handler.LastRequestUri!.ToString());
    }

    [TestMethod]
    public async Task Stream_HttpError_ContextLengthPreservesProviderDetail()
    {
        const string errBody = """
            {"error":{"message":"This endpoint supports a maximum context length of 16384 tokens.","type":"invalid_request_error"}}
            """;

        var events = await Collect(
            Provider(errBody, HttpStatusCode.BadRequest).StreamAsync(MinimalRequest(), CancellationToken.None));

        var err = events.OfType<StreamError>().Single();
        var pex = (ProviderException)err.Ex;
        var ctx = (ContextLengthError)pex.Error;
        StringAssert.Contains(ctx.Detail, "16384");
        StringAssert.Contains(pex.Message, "16384");
    }

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

    // ── Error reporting ───────────────────────────────────────────────────────

    [TestMethod]
    public async Task Stream_HttpError_InvalidModelMapsToModelUnknown()
    {
        const string errBody = """
            {"error":{"message":"invalid model ID","type":"invalid_request_error"}}
            """;

        var events = await Collect(
            Provider(errBody, HttpStatusCode.BadRequest).StreamAsync(MinimalRequest(), CancellationToken.None));

        var err = events.OfType<StreamError>().Single();
        var pex = (ProviderException)err.Ex;
        var req = (ModelUnknownError)pex.Error;
        Assert.AreEqual("invalid model ID", req.Message);
        StringAssert.Contains(pex.Message, "invalid model ID");
    }

    // ── Tool result serialization ─────────────────────────────────────────────

    [TestMethod]
    public async Task ParallelToolResults_EachGetsToolMessage()
    {
        // Assistant fires two tool calls in one turn; the loop returns both results in a
        // single user message. OpenAI requires a tool message for each tool_call_id, so the
        // serialized request must carry both — otherwise the API rejects with HTTP 400.
        var args = System.Text.Json.JsonDocument.Parse("{}").RootElement;
        var request = new ChatRequest("gpt-4o", "sys",
        [
            new UserMessage([new TextBlock("search")]),
            new AssistantMessage(
            [
                new ToolUseBlock("call_A", "Grep", args),
                new ToolUseBlock("call_B", "Read", args),
            ]),
            new UserMessage(
            [
                new ToolResultBlock("call_A", "grep result"),
                new ToolResultBlock("call_B", "read result"),
            ]),
        ], [], 1024);

        var handler = new FakeSseHandler("data: [DONE]\n\n");
        var provider = new OpenAiProvider("test-key", "https://api.openai.com", new HttpClient(handler));

        await Collect(provider.StreamAsync(request, CancellationToken.None));

        var body = handler.LastRequestBody!;
        StringAssert.Contains(body, "call_A");
        StringAssert.Contains(body, "call_B");
        // Both must appear as tool_call_id responses, not just the first.
        var toolMsgCount = System.Text.RegularExpressions.Regex.Matches(body, "\"tool_call_id\"").Count;
        Assert.AreEqual(2, toolMsgCount, $"Expected two tool messages, got body: {body}");
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
