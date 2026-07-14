using Dotsy.Core.Providers;

namespace Dotsy.Core.Loop.Data;

public sealed class TokenUsageTracker
{
    public int TotalInputTokens { get; private set; }
    public int TotalOutputTokens { get; private set; }
    public long TotalGenerationMs { get; private set; }

    public void RecordUsage(UsageUpdate usage, long observedDurationMs = 0)
    {
        TotalInputTokens += usage.InputTokens;
        TotalOutputTokens += usage.OutputTokens;
        // Prefer the server-measured generation time (pure decode speed) over the
        // client-observed stream time, which includes network and prompt evaluation.
        TotalGenerationMs += usage.ServerDurationMs ?? observedDurationMs;
    }

    /// <summary>Average generation speed across all recorded LLM calls; null before any timing data.</summary>
    public double? OutputTokensPerSecond =>
        TotalGenerationMs > 0 ? TotalOutputTokens * 1000.0 / TotalGenerationMs : null;
}
