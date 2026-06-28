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
    /// The loop stopped because the model reached <c>agent.nudge_limit</c> consecutive
    /// non-terminal responses without calling a tool, repeatedly issued the same tool calls,
    /// or reached the consecutive failing-tool guard. A successful or failed tool call resets
    /// the text-only nudge counter; a non-positive nudge limit disables only that counter.
    /// </summary>
    NudgeLimitReached,

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
