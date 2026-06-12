using Dotsy.Providers.OpenAi;

namespace Dotsy.Providers.OpenAiCompatible;

public sealed class OpenAiCompatibleProvider : OpenAiProvider
{
    public override string Name => "compatible";

    public OpenAiCompatibleProvider(string apiKey, string baseUrl, HttpClient? http = null)
        : base(apiKey, baseUrl, http) { }
}
