using Dotsy.Core.Providers;

namespace Dotsy.Core.Loop.Data;

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
    public int TurnCount { get; set; }
}
