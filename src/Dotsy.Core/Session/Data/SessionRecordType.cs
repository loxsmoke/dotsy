using System.Text.Json.Serialization;

namespace Dotsy.Core.Session.Data;

[JsonConverter(typeof(JsonStringEnumConverter<SessionRecordType>))]
public enum SessionRecordType
{
    [JsonStringEnumMemberName("none")]
    None,
    [JsonStringEnumMemberName("user")]
    User,
    [JsonStringEnumMemberName("assistant")]
    Assistant,
    [JsonStringEnumMemberName("tool_result")]
    ToolResult,
    [JsonStringEnumMemberName("summary")]
    Summary,
    [JsonStringEnumMemberName("end")]
    End
}
