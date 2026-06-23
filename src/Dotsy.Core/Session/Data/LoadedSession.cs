using Dotsy.Core.Providers;

namespace Dotsy.Core.Session.Data;

public sealed class LoadedSession
{
    public required string SessionId { get; init; }
    public required List<Message> Messages { get; init; }
    public string? CompactionSummary { get; init; }
    public string? Cwd { get; init; }
}
