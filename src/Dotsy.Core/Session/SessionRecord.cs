namespace Dotsy.Core.Session;

public sealed class SessionRecord
{
    public string Uuid { get; set; } = Guid.NewGuid().ToString();
    public string? ParentUuid { get; set; }
    public string SessionId { get; set; } = "";
    public string Type { get; set; } = ""; // user | assistant | tool_result | summary
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public string Cwd { get; set; } = "";
    public string? GitBranch { get; set; }
    public string Version { get; set; } = "1.0.0";
    public object? Message { get; set; }
    public SessionUsage? Usage { get; set; }
}

public sealed class SessionUsage
{
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int CacheReadTokens { get; set; }
    public int CacheWriteTokens { get; set; }
}

public sealed class SessionIndexEntry
{
    public string SessionId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Cwd { get; set; } = "";
    public string Model { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int MessageCount { get; set; }
}
