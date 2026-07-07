namespace Dotsy.Core.Loop.Data;

/// <summary>
/// Tracks token usage against a model's context window, providing derived metrics
/// for compaction and warning thresholds.
/// </summary>
public record TokenBudget(
    int ContextWindow,  // Context window size of the model, in tokens.
    int ReserveTokens,  // Number of tokens reserved for output, not available for input. Request to LLMs should not exceed ContextWindow - ReserveTokens.
    int KeepRecentTokens, // Number of tokens to keep from recent context during compaction.
    int UsedTokens,     // Number of tokens used so far in this context window.
    float CompactionThreshold) // Fraction of usable budget (window minus reserve) at which compaction should be triggered.
{
    public static readonly TokenBudget Empty = new(200_000, 16_384, 20_000, 0, 0.9f);

    /// <summary>
    /// The number of tokens available for use after subtracting the output reserve from the context window.
    /// </summary>
    public int Usable => ContextWindow - ReserveTokens;
    /// <summary>
    /// The percentage of the context window that has been used.
    /// </summary>
    public float UsagePct => ContextWindow > 0 ? UsedTokens / (float)ContextWindow : 0f;
    // Fraction of the *usable* budget (window minus the output reserve) consumed. The compaction
    // trigger uses this, not UsagePct: with a large reserve the usable budget is exhausted well
    // before UsagePct hits the threshold, so measuring against the full window can let a run die
    // with ContextTooSmall before compaction ever fires.
    public float UsablePct => Usable > 0 ? UsedTokens / (float)Usable : 0f;
    public bool ShouldCompact => Usable > 0 && CompactionThreshold > 0 && UsablePct >= CompactionThreshold;
    public bool ShouldWarn => UsagePct >= 0.6f;

    public TokenBudget WithUsed(int used) => this with { UsedTokens = used };
}
