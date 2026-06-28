using Dotsy.Core.Config;
using Dotsy.Core.Providers;
using Dotsy.Providers.Gemini;
using Dotsy.Providers.Tests.Helpers;

namespace Dotsy.Providers.Tests;

[TestClass]
public sealed class GeminiProviderTests
{
    private static ChatRequest MinimalRequest() =>
        new("gemini-2.5-flash-lite", "sys", [new UserMessage([new TextBlock("hi")])], [], 1024);

    private static async Task DrainAsync(IAsyncEnumerable<ProviderEvent> src)
    {
        await foreach (var _ in src) { }
    }

    [TestMethod]
    public void Name_IsGemini()
    {
        var provider = new GeminiProvider("test-key", http: new HttpClient(new FakeSseHandler("")));
        Assert.AreEqual(ProviderConfig.Gemini, provider.Name);
    }

    [TestMethod]
    public async Task StreamAsync_PostsToGoogleOpenAiCompatibleEndpoint()
    {
        var handler = new FakeSseHandler("data: [DONE]\n\n");
        var provider = new GeminiProvider("test-key", http: new HttpClient(handler));

        await DrainAsync(provider.StreamAsync(MinimalRequest(), CancellationToken.None));

        Assert.AreEqual(
            "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions",
            handler.LastRequestUri?.ToString());
    }

    [TestMethod]
    public async Task StreamAsync_HonorsCustomBaseUrlOverride()
    {
        var handler = new FakeSseHandler("data: [DONE]\n\n");
        var provider = new GeminiProvider("test-key", "https://proxy.example.com/openai/", new HttpClient(handler));

        await DrainAsync(provider.StreamAsync(MinimalRequest(), CancellationToken.None));

        Assert.AreEqual(
            "https://proxy.example.com/openai/chat/completions",
            handler.LastRequestUri?.ToString());
    }

    [TestMethod]
    public async Task GetModelInfo_LoadsLimitsDynamicallyFromNativeEndpoint()
    {
        // Native Gemini models response shape. Distinct numbers prove the value comes from the
        // live response, not the static catalog.
        var handler = new FakeSseHandler("""{"inputTokenLimit":777777,"outputTokenLimit":1234}""");
        var provider = new GeminiProvider("test-key", http: new HttpClient(handler));

        var info = await provider.GetModelInfoAsync("gemini-2.5-flash-lite", CancellationToken.None);

        Assert.AreEqual(777_777, info.ContextWindow);
        Assert.AreEqual(1_234, info.MaxOutputTokens);
        Assert.AreEqual(
            "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash-lite",
            handler.LastRequestUri?.ToString());
    }

    [TestMethod]
    public async Task GetModelInfo_FallsBackToCatalogWhenLiveCallFails()
    {
        // Non-JSON body → live parse fails → falls back to the static catalog (true 1M window).
        var provider = new GeminiProvider("test-key", http: new HttpClient(new FakeSseHandler("not json")));

        var info = await provider.GetModelInfoAsync("gemini-2.5-flash-lite", CancellationToken.None);

        Assert.AreEqual(1_048_576, info.ContextWindow);
    }
}
