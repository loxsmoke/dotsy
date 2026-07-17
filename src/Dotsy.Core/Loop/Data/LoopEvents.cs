using System.Text.Json.Serialization;
using Dotsy.Core.Providers;
using Dotsy.Core.Tools;
using Dotsy.Core.Tools.Interfaces;

namespace Dotsy.Core.Loop.Data;

public abstract record LoopEvent;

public record TextChunk(string Text) : LoopEvent;

public record ThinkingChunk(string Text) : LoopEvent;

public record ToolStarted(int Index, string Name, string Arg) : LoopEvent;

/// <param name="DurationMs">The tool's actual run time, excluding any approval-prompt wait and
/// any time spent queued behind other tools in the same batch.</param>
public record ToolFinished(int Index, string Name, ToolResult Result, long DurationMs = 0) : LoopEvent;

/// <param name="AffectedPaths">Path arguments of this turn's successful write-tool calls
/// (write/edit/multi-edit), as given by the model — relative to the loop cwd or absolute.
/// Lets consumers track agent-modified files even when git can't see them.</param>
public record TurnComplete(
    int TotalTokens,
    bool AnyWriteTools = false,
    IReadOnlyList<string>? AffectedPaths = null) : LoopEvent;

/// <param name="DurationMs">Client-observed duration of the whole LLM stream (network and
/// time-to-first-token included), measured by the agent loop.</param>
/// <param name="ServerDurationMs">Server-measured generation time when the provider reports
/// one (Ollama's eval_duration); null otherwise.</param>
public record TokenUsageUpdated(
    int InputTokens,
    int OutputTokens,
    int CacheReadTokens,
    int CacheWriteTokens,
    long DurationMs = 0,
    long? ServerDurationMs = null) : LoopEvent
{
    public TokenUsageUpdated(UsageUpdate usage, long durationMs = 0)
        : this(usage.InputTokens, usage.OutputTokens, usage.CacheReadTokens, usage.CacheWriteTokens,
               durationMs, usage.ServerDurationMs)
    {}

    /// <summary>Observed throughput over the whole stream; understates pure generation speed.</summary>
    public double? ObservedTokensPerSecond =>
        DurationMs > 0 ? OutputTokens * 1000.0 / DurationMs : null;

    /// <summary>Pure generation speed as measured by the inference server; null when unreported.</summary>
    public double? ServerTokensPerSecond =>
        ServerDurationMs > 0 ? OutputTokens * 1000.0 / ServerDurationMs : null;

    /// <summary>Best available speed figure: server-measured when present, else client-observed.</summary>
    public double? TokensPerSecond => ServerTokensPerSecond ?? ObservedTokensPerSecond;
}

public record CompactionOccurred(int TokensBefore, int TokensAfter, string Summary) : LoopEvent;

/// <summary>
/// Emitted when compaction ran but freed nothing — e.g. everything left is inside the retained
/// recent window, or the summarization request itself failed. <paramref name="Reason"/> explains
/// why so the user isn't left with a bare "nothing to compact" while the context is full.
/// </summary>
public record CompactionSkipped(string Reason) : LoopEvent;

public record LoopEnded(EndReason Reason, string? Message = null) : LoopEvent;

public record PermissionRequired(
    [property: JsonIgnore] ITool Tool,
    string ToolName,
    string DisplayArgument,
    [property: JsonIgnore] TaskCompletionSource<PermissionDecision> Decision) : LoopEvent;

public record RetryScheduled(int AttemptNumber, int MaxAttempts, int DelaySeconds) : LoopEvent;

public record ReflectionOccurred(string Error) : LoopEvent;

/// <summary>
/// Emitted when the loop hits the nudge limit but auto-continue (agent.auto_continue_on_nudge)
/// injects a recovery hint and retries instead of ending. <paramref name="Reason"/> is the
/// stuck condition being recovered from.
/// </summary>
public record AutoContinued(int Attempt, int MaxAttempts, string Reason) : LoopEvent;
