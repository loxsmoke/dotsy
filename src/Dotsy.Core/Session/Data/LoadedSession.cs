using Dotsy.Core.Providers;

namespace Dotsy.Core.Session.Data;

public sealed class LoadedSession
{
    public required string SessionId { get; init; }

    /// <summary>
    /// Messages that make up the model's live context after resume: only what follows the most
    /// recent compaction summary (everything before it is represented by <see cref="CompactionSummary"/>).
    /// </summary>
    public required List<Message> Messages { get; init; }

    /// <summary>
    /// The full saved transcript (every user/assistant/tool message in the file, including those
    /// before the last compaction) for rebuilding the conversation panel so a resumed session looks
    /// like the live run did. Distinct from <see cref="Messages"/>, which drives the model context.
    /// </summary>
    public required List<Message> DisplayMessages { get; init; }

    /// <summary>
    /// Per-tool-call metadata (start time, run duration) keyed by tool_use id, recovered from the
    /// record timestamps and <c>duration_ms</c> fields so restored tool rows show the same timing a
    /// live run did.
    /// </summary>
    public IReadOnlyDictionary<string, RestoredToolInfo> ToolInfo { get; init; } =
        new Dictionary<string, RestoredToolInfo>();

    public string? CompactionSummary { get; init; }
    public string? Cwd { get; init; }

    /// <summary>
    /// Tokens in context at the end of the saved session (input + output of the last assistant
    /// turn that recorded usage), mirroring how <c>AgentLoop</c> tracks the live budget. Lets a
    /// resumed session restore its context-usage figure instead of starting the gauge at 0%.
    /// </summary>
    public int UsedTokens { get; init; }
}

/// <summary>Timing metadata for a restored tool call. <paramref name="DurationMs"/> is the recorded run time.</summary>
public sealed record RestoredToolInfo(DateTimeOffset? StartedAt, int DurationMs);
