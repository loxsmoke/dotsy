using System.Collections.Concurrent;
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
    // Tracks the exit status of the most recent build/test command run via Shell (e.g.
    // `dotnet build`, `dotnet test`, `npm run build`). Null = no build seen yet; false = last
    // build passed; true = last build failed. Used by the completion guard to refuse a Done
    // signal over a red build. See AgentConfig.VerifyBuildBeforeComplete.
    public bool? LastBuildFailed { get; set; }
    // Number of times the completion guard has fired for a failing build, bounding the retries.
    public int BuildGuardTrips { get; set; }
    // Stale-build-state heuristic (see AgentLoopHeuristics.ObserveBuildOutcome): consecutive
    // failed build commands, the distinct error signatures seen across them, and whether the
    // clean-rebuild hint has already been injected for this failure episode. All reset by a
    // passing build.
    public int ConsecutiveBuildFailures { get; set; }
    public List<string> BuildErrorSignatures { get; } = [];
    public bool StaleBuildHintGiven { get; set; }
    // Per-session cache of files already Read, keyed by resolved absolute path, used to de-dupe
    // repeat reads while their content is still live in context. See AgentConfig.DedupeReads and
    // ReadDedup. Concurrent because read-only tools run in parallel.
    public ConcurrentDictionary<string, ReadCacheEntry> ReadCache { get; } =
        new(StringComparer.OrdinalIgnoreCase);
    // Last-known on-disk state (mtime/size) of every file the agent has Read or written this
    // session, keyed by resolved absolute path. Used by ReadBeforeEdit to reject Edit/MultiEdit
    // of a file the model never read or whose disk state is stale. Unlike ReadCache this is
    // always populated, independent of AgentConfig.DedupeReads.
    public ConcurrentDictionary<string, FileFreshnessEntry> FileFreshness { get; } =
        new(StringComparer.OrdinalIgnoreCase);
}
