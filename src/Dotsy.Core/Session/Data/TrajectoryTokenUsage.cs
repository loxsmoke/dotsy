using System.Text.Json.Serialization;

namespace Dotsy.Core.Session.Data;

public sealed class TrajectoryTokenUsage
{
    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; set; }

    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; set; }

    [JsonPropertyName("cache_read_tokens")]
    public int CacheReadTokens { get; set; }

    [JsonPropertyName("cache_write_tokens")]
    public int CacheWriteTokens { get; set; }

    /// <summary>
    /// Cumulative LLM generation time: server-measured (Ollama eval_duration) when available,
    /// otherwise the client-observed stream duration. Unlike the session-level DurationMs this
    /// excludes tool runs and approval waits.
    /// </summary>
    [JsonPropertyName("llm_duration_ms")]
    public long LlmDurationMs { get; set; }

    [JsonPropertyName("output_tokens_per_second")]
    public double? OutputTokensPerSecond =>
        LlmDurationMs > 0 ? Math.Round(OutputTokens * 1000.0 / LlmDurationMs, 1) : null;
}
