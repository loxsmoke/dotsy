namespace Dotsy.Core.Loop.Data;

public enum EndReason
{
    TaskComplete,
    TurnLimitReached,
    NudgeLimitReached,
    ContextTooSmall,
    Cancelled,
    Error
}
