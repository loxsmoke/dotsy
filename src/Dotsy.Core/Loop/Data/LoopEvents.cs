using System.Text.Json.Serialization;
using Dotsy.Core.Tools;
using Dotsy.Core.Tools.Interfaces;

namespace Dotsy.Core.Loop.Data;

public abstract record LoopEvent;

public record TextChunk(string Text) : LoopEvent;

public record ThinkingChunk(string Text) : LoopEvent;

public record ToolStarted(int Index, string Name, string Arg) : LoopEvent;

public record ToolFinished(int Index, string Name, ToolResult Result) : LoopEvent;

public record TurnComplete(int TotalTokens, bool AnyWriteTools = false) : LoopEvent;

public record TokenUsageUpdated(
    int InputTokens,
    int OutputTokens,
    int CacheReadTokens,
    int CacheWriteTokens) : LoopEvent;

public record CompactionOccurred(int TokensBefore, int TokensAfter, string Summary) : LoopEvent;

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
