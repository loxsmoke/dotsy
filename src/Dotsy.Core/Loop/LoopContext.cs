using Dotsy.Core.Providers;

namespace Dotsy.Core.Loop;

/// <summary>
/// Maintains conversation state and metadata for an agent session.
/// Created once per session (new or resumed) and passed through the agent loop.
/// Lifecycle: Created in Program.cs during session setup, populated with messages
/// and configuration, then used throughout AgentLoop.RunAsync() to track conversation
/// history, token usage, compaction summaries, and runtime state like plan mode and reflections.
/// Persisted via SessionStore for resumable sessions.
/// </summary>
public sealed class LoopContext
{
    public string SessionId { get; }

    public LoopContext(string? sessionId = null)
    {
        SessionId = sessionId ?? Guid.NewGuid().ToString();
    }
    public List<Message> Messages { get; } = [];
    public TokenBudget TokenBudget { get; set; } = TokenBudget.Empty;
    public string? CompactionSummary { get; set; }
    public int Reflections { get; set; }
    public bool IsPlanMode { get; set; }
    public List<string> AddedFiles { get; } = [];
    public Dictionary<string, string> LoadedSkills { get; } = [];
    public List<string> TodoItems { get; } = [];
    public int TurnCount { get; set; }
}

public record TokenBudget(
    int ContextWindow,
    int ReserveTokens,
    int KeepRecentTokens,
    int UsedTokens)
{
    public static readonly TokenBudget Empty = new(200_000, 16_384, 20_000, 0);

    public int Usable => ContextWindow - ReserveTokens;
    public float UsagePct => ContextWindow > 0 ? UsedTokens / (float)ContextWindow : 0f;
    public bool ShouldCompact => UsedTokens > Usable;
    public bool ShouldWarn => UsagePct >= 0.60f;

    public TokenBudget WithUsed(int used) => this with { UsedTokens = used };
}
