using System.Text.Json.Serialization;

namespace Dotsy.Core.Session.Data;

public sealed class TrajectoryMetadata
{
    [JsonPropertyName("uuid")]
    public string Uuid { get; set; } = "";

    [JsonPropertyName("dotsy_version")]
    public string DotsyVersion { get; set; } = "1.0.0";

    [JsonPropertyName("cwd")]
    public string Cwd { get; set; } = "";

    [JsonPropertyName("git_branch")]
    public string? GitBranch { get; set; }

    [JsonPropertyName("git_commit")]
    public string? GitCommit { get; set; }

    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "";

    [JsonPropertyName("started_at")]
    public DateTimeOffset StartedAt { get; set; }

    [JsonPropertyName("ended_at")]
    public DateTimeOffset EndedAt { get; set; }

    [JsonPropertyName("duration_ms")]
    public long DurationMs { get; set; }

    [JsonPropertyName("token_usage")]
    public TrajectoryTokenUsage TokenUsage { get; set; } = new();

    [JsonPropertyName("outcome")]
    public string Outcome { get; set; } = "";

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}
