using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dotsy.Core.Session.Data;

public sealed class TrajectoryInputSchema
{
    [JsonPropertyName("jsonSchema")]
    public JsonElement JsonSchema { get; set; }
}
