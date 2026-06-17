using Dotsy.Providers;
using Dotsy.Providers.OpenAi;
using Dotsy.Providers.Tests.Helpers;

namespace Dotsy.Providers.Tests;

[TestClass]
public sealed class ModelCatalogTests
{
    [TestMethod]
    public void TryLookup_KnownOpenAiModel_ReturnsAccurateLimits()
    {
        Assert.IsTrue(ModelCatalog.TryLookup("gpt-4.1-nano", out var info));
        Assert.AreEqual(1_047_576, info.ContextWindow);
        Assert.AreEqual(32_768, info.MaxOutputTokens);
        Assert.AreEqual("gpt-4.1-nano", info.Id);
    }

    [TestMethod]
    public void TryLookup_KnownGeminiModel_ReturnsAccurateLimits()
    {
        Assert.IsTrue(ModelCatalog.TryLookup("gemini-2.5-flash-lite", out var info));
        Assert.AreEqual(1_048_576, info.ContextWindow);
        Assert.AreEqual(65_536, info.MaxOutputTokens);
    }

    [TestMethod]
    public void TryLookup_MostSpecificPrefixWins()
    {
        // "gpt-4o" must not be shadowed by the generic "gpt-4" entry.
        Assert.IsTrue(ModelCatalog.TryLookup("gpt-4o", out var info));
        Assert.AreEqual(128_000, info.ContextWindow);

        Assert.IsTrue(ModelCatalog.TryLookup("gpt-4", out var plain));
        Assert.AreEqual(8_192, plain.ContextWindow);
    }

    [TestMethod]
    public void TryLookup_UnknownModel_ReturnsFalse()
    {
        Assert.IsFalse(ModelCatalog.TryLookup("some-local-llama", out _));
        Assert.IsFalse(ModelCatalog.TryLookup("", out _));
    }

    [TestMethod]
    public async Task OpenAiProvider_GetModelInfo_KnownModelIsAccurate()
    {
        var provider = new OpenAiProvider("test-key", "https://api.openai.com", new HttpClient(new FakeSseHandler("")));
        var info = await provider.GetModelInfoAsync("gpt-4.1-nano", CancellationToken.None);
        Assert.AreEqual(1_047_576, info.ContextWindow);
    }
}
