using System.Text.Json.Nodes;
using Dotsy.Core.Providers;
using Dotsy.Providers.OpenAi;

namespace Dotsy.Providers.AzureOpenAi;

public sealed class AzureOpenAiProvider : OpenAiProvider
{
    private readonly string _endpoint;
    private readonly string _deployment;
    private readonly string _apiVersion;

    public override string Name => "azure";

    protected override string ChatEndpoint =>
        $"/openai/deployments/{_deployment}/chat/completions?api-version={_apiVersion}";

    public AzureOpenAiProvider(
        string apiKey,
        string endpoint,
        string deployment,
        string apiVersion = "2025-01-01",
        HttpClient? http = null)
        : base(apiKey, endpoint, http)
    {
        _endpoint = endpoint;
        _deployment = deployment;
        _apiVersion = apiVersion;
        // Azure uses api-key header instead of Bearer
        Http.DefaultRequestHeaders.Authorization = null;
        Http.DefaultRequestHeaders.Add("api-key", apiKey);
    }
}
