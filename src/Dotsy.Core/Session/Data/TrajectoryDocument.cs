using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Dotsy.Core.Session.Data;

public sealed class TrajectoryDocument
{
    [JsonPropertyName("question_category")]
    public string QuestionCategory { get; set; } = "unknown";

    [JsonPropertyName("complexity_level")]
    public string ComplexityLevel { get; set; } = "unknown";

    [JsonPropertyName("question")]
    public string Question { get; set; } = "";

    [JsonPropertyName("agent_prompt")]
    public string AgentPrompt { get; set; } = "";

    [JsonPropertyName("enabled_tools")]
    public List<string> EnabledTools { get; set; } = [];

    [JsonPropertyName("skills_path")]
    public string SkillsPath { get; set; } = "";

    [JsonPropertyName("uuid")]
    public string Uuid { get; set; } = "";

    [JsonPropertyName("messages")]
    public List<JsonObject> Messages { get; set; } = [];

    [JsonPropertyName("tools")]
    public List<TrajectoryToolRow> Tools { get; set; } = [];

    [JsonPropertyName("metadata")]
    public TrajectoryMetadata Metadata { get; set; } = new();

    [JsonPropertyName("hf_split")]
    public string HfSplit { get; set; } = "dotsy";
}
