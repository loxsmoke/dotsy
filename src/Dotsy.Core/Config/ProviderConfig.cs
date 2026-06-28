namespace Dotsy.Core.Config;

public static class ProviderConfig
{
    #region Provider Names
    public const string Anthropic = "anthropic";
    public const string OpenAi = "openai";
    public const string Azure = "azure";
    public const string AzureOpenAi = "azure_openai";
    public const string Ollama = "ollama";
    public const string Compatible = "compatible";
    public const string Gemini = "gemini";
    #endregion

    #region Provider environment variable names
    public const string AnthropicEnvVar = "ANTHROPIC_API_KEY";
    public const string OpenAiEnvVar = "OPENAI_API_KEY";
    public const string AzureEnvVar = "AZURE_OPENAI_API_KEY";
    public const string CompatibleEnvVar = "OPENAI_API_KEY";
    public const string GeminiEnvVar = "GEMINI_API_KEY";
    #endregion

    public static readonly IReadOnlyList<string> SelectableProviders =
    [
        Anthropic,
        OpenAi,
        Ollama,
        AzureOpenAi,
        Compatible,
        Gemini
    ];

    public static string? ProviderEnvVar(string provider) => provider.ToLowerInvariant() switch
    {
        Anthropic => AnthropicEnvVar,
        OpenAi => OpenAiEnvVar,
        Azure => AzureEnvVar,
        AzureOpenAi => AzureEnvVar,
        Compatible => CompatibleEnvVar,
        Gemini => GeminiEnvVar,
        _ => null
    };

    public static string GetProviderDisplayName(string providerKey) =>
    providerKey.ToLowerInvariant() switch
    {
        Anthropic => "Anthropic",
        OpenAi => "OpenAI",
        Ollama => "Ollama",
        Azure => "Azure",
        AzureOpenAi => "Azure OpenAI",
        Gemini => "Gemini",
        _ => providerKey
    };


}
