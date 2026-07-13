using Dotsy.Core.Providers;

namespace Dotsy.Providers;

/// <summary>
/// Known context-window and max-output token limits for common models, keyed by model-id prefix.
/// The OpenAI-family providers (OpenAI, Azure, OpenAI-compatible, Gemini) authenticate against a
/// <c>/v1/models</c> endpoint that does <b>not</b> report token limits, so accurate values for those
/// providers come from this table. (Anthropic reports <c>context_window</c> / <c>max_output_tokens</c>
/// live from its own API, so it does not rely on this catalog.)
///
/// Entries are ordered most-specific first; the first prefix match wins. Limits reflect each model
/// family's documented totals — update here when families change.
/// </summary>
public static class ModelCatalog
{
    private static readonly (string Prefix, int ContextWindow, int MaxOutput)[] Entries =
    [
        // ── OpenAI ────────────────────────────────────────────────────────────
        ("gpt-4.1",        1_047_576, 32_768),  // gpt-4.1, -mini, -nano
        ("gpt-4o-mini",      128_000, 16_384),
        ("gpt-4o",           128_000, 16_384),
        ("gpt-4-turbo",      128_000,  4_096),
        ("gpt-4",              8_192,  4_096),
        ("gpt-3.5-turbo",     16_385,  4_096),
        ("o4-mini",          200_000, 100_000),
        ("o3",               200_000, 100_000),
        ("o1",               200_000, 100_000),

        // ── Google Gemini ─────────────────────────────────────────────────────
        ("gemini-2.5",     1_048_576, 65_536),  // 2.5 pro / flash / flash-lite
        ("gemini-2.0",     1_048_576,  8_192),  // 2.0 flash / flash-lite
        ("gemini-1.5-pro", 2_097_152,  8_192),
        ("gemini-1.5",     1_048_576,  8_192),

        // ── Qwen via OpenAI-compatible routers ─────────────────────────────────────────────
        ("qwen/qwen3.6-35b-a3b", 262_144, 32_768),
    ];

    /// <summary>Returns accurate <see cref="ModelInfo"/> for a known model id, or false if unknown.</summary>
    public static bool TryLookup(string modelId, out ModelInfo info)
    {
        if (!string.IsNullOrWhiteSpace(modelId))
        {
            var id = modelId.Trim().ToLowerInvariant();
            foreach (var (prefix, ctx, max) in Entries)
            {
                if (id.StartsWith(prefix, StringComparison.Ordinal))
                {
                    info = new ModelInfo(modelId, ctx, max, ModelInfoSource.Catalog);
                    return true;
                }
            }
        }
        info = null!;
        return false;
    }
}
