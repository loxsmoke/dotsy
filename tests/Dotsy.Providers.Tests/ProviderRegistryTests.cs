using Dotsy.Core.Config;
using Dotsy.Core.Providers;
using Dotsy.Providers.Anthropic;
using Dotsy.Providers.AzureOpenAi;
using Dotsy.Providers.Gemini;
using Dotsy.Providers.Ollama;
using Dotsy.Providers.OpenAi;
using Dotsy.Providers.OpenAiCompatible;

namespace Dotsy.Providers.Tests;

[TestClass]
public sealed class ProviderRegistryTests
{
    [TestMethod]
    [DataRow("anthropic", typeof(AnthropicProvider))]
    [DataRow("openai", typeof(OpenAiProvider))]
    [DataRow("azure", typeof(AzureOpenAiProvider))]
    [DataRow("ollama", typeof(OllamaProvider))]
    [DataRow("compatible", typeof(OpenAiCompatibleProvider))]
    [DataRow("gemini", typeof(GeminiProvider))]
    public void Resolve_CreatesProviderForConfiguredProvider(string providerName, Type expectedType)
    {
        using var http = new HttpClient(new EmptyHandler());
        var provider = ProviderRegistry.Resolve(Config(providerName), http);

        Assert.IsInstanceOfType(provider, expectedType);
        Assert.IsInstanceOfType<IProvider>(provider);
    }

    [TestMethod]
    public void Resolve_ProviderNameMatchingIsCaseInsensitive()
    {
        using var http = new HttpClient(new EmptyHandler());
        var provider = ProviderRegistry.Resolve(Config("OpenAI"), http);

        Assert.IsInstanceOfType<OpenAiProvider>(provider);
    }

    [TestMethod]
    public void Resolve_UnknownProviderThrows()
    {
        using var http = new HttpClient(new EmptyHandler());

        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => ProviderRegistry.Resolve(Config("made-up"), http));

        StringAssert.Contains(ex.Message, "Unknown provider: made-up");
    }

    private static DotsyConfig Config(string provider) => new()
    {
        Model = new ModelConfig
        {
            Provider = provider,
            Anthropic = new AnthropicConfig { ApiKey = "anthropic-key", Id = "claude" },
            OpenAi = new OpenAiConfig { ApiKey = "openai-key", Id = "gpt", BaseUrl = "https://openai.example.test" },
            Azure = new AzureConfig
            {
                ApiKey = "azure-key",
                Id = "azure-model",
                Endpoint = "https://azure.example.test",
                Deployment = "deployment",
                ApiVersion = "2025-01-01"
            },
            Ollama = new OllamaConfig { Id = "llama", BaseUrl = "http://localhost:11434", MaxContextTokens = 8192 },
            Compatible = new CompatibleConfig
            {
                ApiKey = "compatible-key",
                Id = "compatible-model",
                BaseUrl = "https://compatible.example.test"
            },
            Gemini = new GeminiConfig { ApiKey = "gemini-key", Id = "gemini" }
        }
    };

    private sealed class EmptyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }
}
