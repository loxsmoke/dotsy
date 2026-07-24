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
            "offset": { "type": "integer", "default": 0, "description": "0-based line number to start reading from." },
            "limit":  { "type": "integer", "default": 2000, "description": "Maximum number of lines to read." },
            "start_line": { "type": "integer", "description": "1-based first line to read, inclusive. Alternative to offset; use with end_line." },
            "end_line":   { "type": "integer", "description": "1-based last line to read, inclusive. Alternative to limit; use with start_line." },
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
            "pattern":       { "type": "string", "description": "Regex pattern to search for." },
            "path":          { "type": "string", "description": "Directory or file to search IN. Defaults to the working directory. Must exist. Do not put a glob here (use the glob field), and do not put a directory you want to skip here (use the exclude field)." },
            "glob":          { "type": "string", "description": "Glob of which files to INCLUDE, e.g. '*.cs' or '**/*.{ts,tsx}'." },
            "exclude":       { "type": "string", "description": "Glob of files/directories to EXCLUDE from the search, e.g. 'terminal.gui' or '**/bin/**'. Use this to skip a directory instead of pointing path at it." },
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
            "pattern": { "type": "string", "description": "Glob of files to match, e.g. '**/*.cs' or '*.{ts,tsx}'." },
            "path":    { "type": "string", "description": "Directory to search in. Defaults to the working directory." },
            "exclude": { "type": "string", "description": "Glob of files/directories to EXCLUDE, e.g. 'extern' or '**/test/**'. Build and VCS dirs (.git, bin, obj, node_modules) are skipped by default." }
          },
          "required": ["pattern"]
        }
        """);

    public static readonly JsonElement FindDefsSchema = Parse("""
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "What to outline: an existing C# file, a directory (outlined recursively), a glob such as \"**/*Service.cs\", or a bare file name to search for under the working directory." }
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
            "action": {
              "type": "string",
              "enum": ["list", "add", "update", "section"],
              "description": "Defaults to list when omitted. list: show sections and tasks with their indexes. add: add a task. update: rewrite, complete, or delete a task by index. section: create, rename, or delete a section."
            },
            "text": {
              "type": "string",
              "description": "add/update: the task text, without the checkbox prefix."
            },
            "task": {
              "type": "integer",
              "description": "update: the task index shown by list."
            },
            "done": {
              "type": "boolean",
              "description": "add/update: true marks the task [x], false marks it [ ]."
            },
            "delete": {
              "type": "boolean",
              "description": "update/section: true deletes the task or section."
            },
            "section": {
              "type": "string",
              "description": "Section title, leading number, or index. add: target section, created if missing (omit to use the last section). list: filter to one section. section: the existing section to rename or delete."
            },
            "title": {
              "type": "string",
              "description": "section: the heading text (the new name when renaming), without leading # characters."
            },
            "status": {
              "type": "string",
              "enum": ["todo", "done", "all"],
              "description": "list: filter tasks by completion. Defaults to all."
            }
          },
          "required": []
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
            "description": { "type": "string", "description": "Short human-readable summary of the sub-agent's assignment (a few words)." },
            "prompt":      { "type": "string", "description": "Full instructions for the sub-agent: the coding work to do, relevant paths, and what to report back." },
            "task_id":     { "type": "string", "description": "Only for checking a previously launched sub-agent: the id returned by that launch. Never invent one; omit when launching new work." }
          }
        }
        """);
}
