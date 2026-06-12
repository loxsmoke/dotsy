namespace Dotsy.Core.Providers;

public abstract record ProviderEvent;

public record TextDelta(string Text) : ProviderEvent;

public record ThinkingDelta(string Text) : ProviderEvent;

public record ToolCallDelta(string Id, string Name, string ArgumentsJson) : ProviderEvent;

public record UsageUpdate(
    int InputTokens,
    int OutputTokens,
    int CacheReadTokens,
    int CacheWriteTokens) : ProviderEvent;

public record StreamEnd(StopReason Reason) : ProviderEvent;

public record StreamError(Exception Ex) : ProviderEvent;

public enum StopReason { EndTurn, ToolUse, MaxTokens, StopSequence, Error }
