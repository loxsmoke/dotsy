using System.Text.Json;

namespace Dotsy.Core.Tools;

/// <summary>
/// Contains JSON schema definitions for tool parameters and validation.
/// </summary>
internal static class ToolSchemas
{
    public static JsonElement Parse(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    public static readonly JsonElement ReadSchema = Parse("""
        {
          "type": "object",
          "properties": {
            "path":   { "type": "string" },
            "offset": { "type": "integer", "default": 0 },
            "limit":  { "type": "integer", "default": 2000 },
            "include_diff": { "type": "boolean", "default": false }
          },
          "required": ["path"]
        }
        """);

    public static readonly JsonElement WriteSchema = Parse("""
        {
          "type": "object",
          "properties": {
            "path":    { "type": "string" },
            "content": { "type": "string" }
          },
          "required": ["path", "content"]
        }
        """);

    public static readonly JsonElement EditSchema = Parse("""
        {
          "type": "object",
          "properties": {
            "path":        { "type": "string" },
            "new_string":  { "type": "string" },
            "start_line":  { "type": "integer", "description": "1-based first line to replace, inclusive. Must be provided with end_line." },
            "end_line":    { "type": "integer", "description": "1-based last line to replace, inclusive. Must be provided with start_line." }
          },
          "required": ["path", "start_line", "end_line", "new_string"]
        }
        """);

    public static readonly JsonElement MultiEditSchema = Parse("""
        {
          "type": "object",
          "properties": {
            "path": { "type": "string" },
            "edits": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "new_string":  { "type": "string" },
                  "start_line":  { "type": "integer", "description": "1-based first line to replace, inclusive." },
                  "end_line":    { "type": "integer", "description": "1-based last line to replace, inclusive." }
                },
                "required": ["start_line", "end_line", "new_string"]
              }
            }
          },
          "required": ["path", "edits"]
        }
        """);

    public static readonly JsonElement ListSchema = Parse("""
        {
          "type": "object",
          "properties": {
            "path":      { "type": "string" },
            "recursive": { "type": "boolean", "default": false }
          },
          "required": ["path"]
        }
        """);

    public static readonly JsonElement GrepSchema = Parse("""
        {
          "type": "object",
          "properties": {
            "pattern":       { "type": "string" },
            "path":          { "type": "string" },
            "context_lines": { "type": "integer", "default": 0 },
            "ignore_case":   { "type": "boolean", "default": false }
          },
          "required": ["pattern"]
        }
        """);

    public static readonly JsonElement GlobSchema = Parse("""
        {
          "type": "object",
          "properties": {
            "pattern": { "type": "string" },
            "path":    { "type": "string" }
          },
          "required": ["pattern"]
        }
        """);

    public static readonly JsonElement FindDefinitionsSchema = Parse("""
        {
          "type": "object",
          "properties": {
            "path": { "type": "string" }
          },
          "required": ["path"]
        }
        """);

    public static readonly JsonElement ShellSchema = Parse("""
        {
          "type": "object",
          "properties": {
            "command":    { "type": "string" },
            "timeout_ms": { "type": "integer", "default": 30000 }
          },
          "required": ["command"]
        }
        """);

    public static readonly JsonElement WebFetchSchema = Parse("""
        {
          "type": "object",
          "properties": {
            "url": { "type": "string" }
          },
          "required": ["url"]
        }
        """);

    public static readonly JsonElement WebSearchSchema = Parse("""
        {
          "type": "object",
          "properties": {
            "query": { "type": "string" }
          },
          "required": ["query"]
        }
        """);

    public static readonly JsonElement SkillSchema = Parse("""
        {
          "type": "object",
          "properties": {
            "name": { "type": "string" }
          },
          "required": ["name"]
        }
        """);

    public static readonly JsonElement TodoSchema = Parse("""
        {
          "type": "object",
          "properties": {
            "items": {
              "type": "array",
              "items": { "type": "string" }
            }
          },
          "required": ["items"]
        }
        """);

    public static readonly JsonElement AskSchema = Parse("""
        {
          "type": "object",
          "properties": {
            "question": { "type": "string" }
          },
          "required": ["question"]
        }
        """);

    public static readonly JsonElement DoneSchema = Parse("""
        {
          "type": "object",
          "properties": {
            "summary": { "type": "string" }
          },
          "required": ["summary"]
        }
        """);

    public static readonly JsonElement TaskSchema = Parse("""
        {
          "type": "object",
          "properties": {
            "description": { "type": "string" },
            "prompt":      { "type": "string" },
            "task_id":     { "type": "string", "description": "Existing task id to check status/result." }
          }
        }
        """);
}
