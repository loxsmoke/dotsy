using Dotsy.Core.Config;
using Dotsy.Core.Providers;
using Dotsy.Providers.Anthropic;
using Dotsy.Providers.AzureOpenAi;
using Dotsy.Providers.Ollama;
using Dotsy.Providers.Gemini;
using Dotsy.Providers.OpenAi;
using Dotsy.Providers.OpenAiCompatible;

namespace Dotsy.Providers;

public static class ProviderRegistry
{
    public static IProvider Resolve(DotsyConfig config, HttpClient? http = null)
    {
        var model = config.Model;
        var apiKeyOverrideVariableName = ProviderConfig.ProviderEnvVar(model.Provider);

        var apiKey = FirstNonEmpty(
            model.ActiveModel.ApiKey,
            (apiKeyOverrideVariableName != null ? Environment.GetEnvironmentVariable(apiKeyOverrideVariableName) : "") ?? "");

        return model.Provider.ToLowerInvariant() switch
        {
            ProviderConfig.Anthropic => new AnthropicProvider(apiKey, http),
            ProviderConfig.OpenAi => new OpenAiProvider(apiKey, model.OpenAi.BaseUrl, http),
            ProviderConfig.Azure or ProviderConfig.AzureOpenAi => new AzureOpenAiProvider(
                apiKey,
                model.Azure.Endpoint,
                model.Azure.Deployment,
                model.Azure.ApiVersion,
                http),
            ProviderConfig.Ollama => new OllamaProvider(model.Ollama.BaseUrl, http, model.Ollama.MaxContextTokens),
            ProviderConfig.Compatible => new OpenAiCompatibleProvider(apiKey, model.Compatible.BaseUrl, http),
            ProviderConfig.Gemini => new GeminiProvider(apiKey, http: http),
            _ => throw new InvalidOperationException($"Unknown provider: {model.Provider}")
        };
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "";
}
