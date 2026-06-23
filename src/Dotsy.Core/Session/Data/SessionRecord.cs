namespace Dotsy.Core.Session.Data;

public sealed class SessionRecord
{
    /// <summary>
    /// A unique identifier for this record. This is generated automatically when the record is created.
    /// </summary>
    public string Uuid { get; set; } = Guid.NewGuid().ToString();
    /// <summary>
    /// The UUID of the parent record, if this record is a child of another record. 
    /// This is used to create a tree structure of records, where each record can have multiple children. 
    /// If this record is a root record, this property will be null.
    /// </summary>
    public string? ParentUuid { get; set; }
    /// <summary>
    /// The session ID that this record belongs to. This is used to group records together into a single session.
    /// </summary>
    public string SessionId { get; set; } = "";
    /// <summary>
    /// The type of this record. This can be "user", "assistant", "tool_result", or "summary".
    /// </summary>
    public SessionRecordType Type { get; set; } = SessionRecordType.None;
    /// <summary>
    /// The timestamp of when this record was created. This is generated automatically when the record is created.
    /// </summary>
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;    
    /// <summary>
    /// The current working directory when this record was created.
    /// </summary>
    public string Cwd { get; set; } = "";
    /// <summary>
    /// The Git branch that was checked out when this record was created.
    /// </summary>
    public string? GitBranch { get; set; }
    /// <summary>
    /// The version of the application that created this record.
    /// </summary>
    public string Version { get; set; } = "1.0.0";
    /// <summary>
    /// The message associated with this record. This can be any object.
    /// </summary>
    public object? Message { get; set; }
    /// <summary>
    /// Token usage information for this record. This is only applicable for records of type "assistant" or "tool_result".
    /// </summary>
    public SessionUsage? Usage { get; set; }
}
