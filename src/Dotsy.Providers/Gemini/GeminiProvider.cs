using System.Text.Json;
using Dotsy.Core.Config;
using Dotsy.Core.Providers;
using Dotsy.Providers.OpenAi;

namespace Dotsy.Providers.Gemini;

/// <summary>
/// Google Gemini via its OpenAI-compatible endpoint. Behaves exactly like the OpenAI
/// provider but with Google's base URL baked in, so users only set an id and api_key —
/// no base_url needed. Auth uses the value named by <see cref="ProviderConfig.GeminiEnvVar"/>.
/// </summary>
public sealed class GeminiProvider : OpenAiProvider
{
    public const string DefaultBaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai/";

    public override string Name => ProviderConfig.Gemini;

    // BaseAddress ends in "/openai/"; a relative endpoint appends to it, giving
    // ".../v1beta/openai/chat/completions" (the OpenAI-compatible chat route).
    protected override string ChatEndpoint => "chat/completions";

    public GeminiProvider(string apiKey, string? baseUrl = null, HttpClient? http = null)
        : base(apiKey, string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl, http) { }

    // DYNAMIC limits. Unlike the OpenAI-compatible layer, Gemini's *native* models endpoint
    // (GET .../v1beta/models/{id}) reports real inputTokenLimit / outputTokenLimit, so load them
    // live. Falls back to the static catalog (then the generic default) if the call fails.
    public override async Task<ModelInfo> GetModelInfoAsync(string modelId, CancellationToken ct)
    {
        try
        {
            // From ".../v1beta/openai/" go up one level to ".../v1beta/models/{id}".
            var url = new Uri(Http.BaseAddress!, $"../models/{modelId}");
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("x-goog-api-key", ApiKey);

            var resp = await Http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync(ct);
                var root = JsonDocument.Parse(json).RootElement;
                int ctx = root.TryGetProperty("inputTokenLimit", out var i) ? i.GetInt32() : 0;
                int max = root.TryGetProperty("outputTokenLimit", out var o) ? o.GetInt32() : 0;
                if (ctx > 0)
                    return new ModelInfo(modelId, ctx, max > 0 ? max : 8_192, ModelInfoSource.Api);
            }
        }
        catch { }

        // base resolves via ModelCatalog first, then the OpenAI-family generic default.
        return await base.GetModelInfoAsync(modelId, ct);
    }
}
