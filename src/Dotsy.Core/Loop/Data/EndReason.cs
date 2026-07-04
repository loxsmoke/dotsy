namespace Dotsy.Core.Loop.Data;

public enum EndReason
{
    /// <summary>
    /// A completion-signal tool, such as Done, reported that the requested task is complete.
    /// </summary>
    TaskComplete,

    /// <summary>
    /// The model finished a normal text response without requesting any tools.
    /// </summary>
    ResponseComplete,

    /// <summary>
    /// The agent completed the configured maximum number of turns before signaling completion.
    /// A non-positive <c>agent.max_turns</c> disables this limit.
    /// </summary>
    TurnLimitReached,

    /// <summary>
    /// Legacy catch-all for the nudge/loop guards. Retained for backward compatibility with
    /// older session logs; the loop no longer emits it. New runs report the specific cause via
    /// <see cref="NoProgress"/>, <see cref="MaxTokens"/>, <see cref="Repetition"/>, or
    /// <see cref="ToolErrorStreak"/>.
    /// </summary>
    NudgeLimitReached,

    /// <summary>
    /// The model reached <c>agent.nudge_limit</c> consecutive responses that neither called a
    /// tool nor cleanly ended the turn, and auto-continue (if enabled) did not recover it. The
    /// model produced no new work to act on.
    /// </summary>
    NoProgress,

    /// <summary>
    /// The model's response was cut off by the provider's output token limit
    /// (<c>StopReason.MaxTokens</c>) repeatedly, without making tool-call progress in between.
    /// </summary>
    MaxTokens,

    /// <summary>
    /// The model kept issuing the same tool calls — either identical to the previous turn or
    /// cycling within the rolling window — without making progress, tripping the loop guard.
    /// </summary>
    Repetition,

    /// <summary>
    /// Consecutive turns of failing tool calls exceeded the guard threshold; the model was not
    /// recovering from the errors.
    /// </summary>
    ToolErrorStreak,

    /// <summary>
    /// The conversation no longer fit in the model's context window and compaction was
    /// disabled, could not reduce it sufficiently, or had already been retried.
    /// </summary>
    ContextTooSmall,

    /// <summary>
    /// The supplied cancellation token was cancelled before the loop completed.
    /// </summary>
    Cancelled,

    /// <summary>
    /// The provider request failed for a reason other than an exhausted context window.
    /// </summary>
    Error
}
