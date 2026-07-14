namespace Dotsy.Core.Providers;

public abstract record ProviderEvent;

public record TextDelta(string Text) : ProviderEvent;

public record ThinkingDelta(string Text) : ProviderEvent;

public record ToolCallDelta(string Id, string Name, string ArgumentsJson) : ProviderEvent;

/// <param name="ServerDurationMs">Generation time measured by the inference server itself
/// (Ollama's eval_duration); null for providers that don't report one. Excludes network
/// latency and prompt evaluation, so OutputTokens over this is pure generation speed.</param>
public record UsageUpdate(
    int InputTokens,
    int OutputTokens,
    int CacheReadTokens,
    int CacheWriteTokens,
    long? ServerDurationMs = null) : ProviderEvent;

public record StreamEnd(StopReason Reason) : ProviderEvent;

public record StreamError(Exception Ex) : ProviderEvent;

public enum StopReason { EndTurn, ToolUse, MaxTokens, StopSequence, Error }
