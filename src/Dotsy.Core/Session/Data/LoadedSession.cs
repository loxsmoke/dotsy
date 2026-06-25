using Dotsy.Core.Providers;

namespace Dotsy.Core.Session.Data;

public sealed class LoadedSession
{
    public required string SessionId { get; init; }
    public required List<Message> Messages { get; init; }
    public string? CompactionSummary { get; init; }
    public string? Cwd { get; init; }

    /// <summary>
    /// Tokens in context at the end of the saved session (input + output of the last assistant
    /// turn that recorded usage), mirroring how <c>AgentLoop</c> tracks the live budget. Lets a
    /// resumed session restore its context-usage figure instead of starting the gauge at 0%.
    /// </summary>
    public int UsedTokens { get; init; }
}
