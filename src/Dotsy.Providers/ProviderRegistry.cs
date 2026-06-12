using Dotsy.Core.Config;
using Dotsy.Core.Providers;
using Dotsy.Providers.Anthropic;
using Dotsy.Providers.AzureOpenAi;
using Dotsy.Providers.Ollama;
using Dotsy.Providers.OpenAi;
using Dotsy.Providers.OpenAiCompatible;

namespace Dotsy.Providers;

public static class ProviderRegistry
{
    public static IProvider Resolve(DotsyConfig config, HttpClient? http = null)
    {
        var model = config.Model;
        var apiKey = ResolveApiKey(model);

        return model.Provider.ToLowerInvariant() switch
        {
            "anthropic" => new AnthropicProvider(apiKey, http),
            "openai" => new OpenAiProvider(apiKey, model.OpenAi.BaseUrl, http),
            "azure" => new AzureOpenAiProvider(
                apiKey,
                model.Azure.Endpoint,
                model.Azure.Deployment,
                model.Azure.ApiVersion,
                http),
            "ollama" => new OllamaProvider(model.Ollama.BaseUrl, http),
            "compatible" => new OpenAiCompatibleProvider(apiKey, model.Compatible.BaseUrl, http),
            _ => throw new InvalidOperationException($"Unknown provider: {model.Provider}")
        };
    }

    private static string ResolveApiKey(ModelConfig model) => model.Provider.ToLowerInvariant() switch
    {
        "anthropic" => FirstNonEmpty(
            model.Anthropic.ApiKey,
            Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? ""),
        "openai" => FirstNonEmpty(
            model.OpenAi.ApiKey,
            Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? ""),
        "azure" => FirstNonEmpty(
            model.Azure.ApiKey,
            Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY") ?? ""),
        "compatible" => FirstNonEmpty(
            model.Compatible.ApiKey,
            Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? ""),
        _ => ""
    };

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "";
}
