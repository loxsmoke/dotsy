using System.Text.Json.Serialization;

namespace Dotsy.Core.Session.Data;

public sealed class TrajectoryTokenUsage
{
    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; set; }

    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; set; }

    [JsonPropertyName("cache_read_tokens")]
    public int CacheReadTokens { get; set; }

    [JsonPropertyName("cache_write_tokens")]
    public int CacheWriteTokens { get; set; }
}
