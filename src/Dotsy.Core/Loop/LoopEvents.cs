using Dotsy.Core.Tools;

namespace Dotsy.Core.Loop;

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
    string ToolName,
    string DisplayArgument,
    TaskCompletionSource<PermissionDecision> Decision) : LoopEvent;

public record RetryScheduled(int AttemptNumber, int MaxAttempts, int DelaySeconds) : LoopEvent;

public record ReflectionOccurred(string Error) : LoopEvent;

public enum EndReason
{
    TaskComplete,
    TurnLimitReached,
    NudgeLimitReached,
    ContextTooSmall,
    Cancelled,
    Error
}

public enum PermissionDecision { AllowOnce, AllowForProject, AlwaysAllow, Deny }
