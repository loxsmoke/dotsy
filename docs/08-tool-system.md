## 8. Tool System

### 8.1 Interface

```csharp
public interface ITool
{
    string Name { get; }
    string Description { get; }       // injected into system prompt tool list
    JsonElement InputSchema { get; }   // JSON Schema for the LLM
    ToolSafety Safety { get; }         // ReadOnly | Sequential | Destructive
    bool IsCompletionSignal { get; }   // true only for "done" tool
    Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext ctx, CancellationToken ct);
}
```

### 8.2 Built-in Tools

#### File Operations

| Tool | Description | Key params |
|------|-------------|-----------|
| `read` | Read a file; paginates via offset/limit; handles text, images | `path`, `offset?`, `limit?` |
| `write` | Write or overwrite a file; creates parent dirs | `path`, `content` |
| `edit` | Exact line-range replacement using 1-based inclusive line numbers | `path`, `start_line`, `end_line`, `new_string` |
| `multi_edit` | Multiple non-overlapping line-range replacements in one call | `path`, `edits[]` with `start_line`, `end_line`, `new_string` |
| `list` | List directory contents; marks dirs with `/` | `path`, `recursive?` |

#### Search

| Tool | Description | Key params |
|------|-------------|-----------|
| `grep` | Ripgrep regex search; respects .gitignore | `pattern`, `path?`, `glob?`, `context_lines?` |
| `glob` | File-pattern search sorted by mtime | `pattern`, `path?` |
| `find_definitions` | Roslyn: top-level types and members in a directory | `path`, `language?` |

#### Execution

| Tool | Description | Key params |
|------|-------------|-----------|
| `shell` | Run a shell command; captures stdout+stderr; timeout | `command`, `cwd?`, `timeout_ms?` |

#### Web

| Tool | Description | Key params |
|------|-------------|-----------|
| `webfetch` | Fetch URL as Markdown (via html-to-markdown); HTTPS-upgrades HTTP | `url`, `max_bytes?` |
| `websearch` | Web search; returns titles + snippets + URLs | `query`, `num_results?` |

#### Agent Utilities

| Tool | Description | Key params |
|------|-------------|-----------|
| `skill` | Load a named skill by name | `name` |
| `todo` | Create/update a structured task list | `items[]` |
| `ask` | Ask the user a clarifying question with optional choices | `question`, `options?` |
| `done` | Signal task completion (is_completion_signal = true) | `summary` |
| `task` | Delegate to a background subagent (returns task_id) | `prompt`, `tools?` |

#### MCP Passthrough

| Tool | Description |
|------|-------------|
| `mcp_call` | Call a tool on a connected MCP server |

### 8.3 Tool Registry

`ToolRegistry` holds all registered `ITool` instances. At loop start, the registry serialises all tool definitions to JSON Schema and passes them in `ChatRequest.Tools`. Tools are looked up by `Name` when the model emits a tool call.

External tools (MCP) are registered at session start after MCP server discovery.

### 8.4 Tool Output Size Limits

Tool output limits are part of the tool contract because large results affect model context, provider payload size, and follow-up tool planning. Tools that truncate output must include a machine-readable marker so the model knows the result is incomplete.

#### Shell Output

Shell output is limited to **30 000 characters**. If combined stdout + stderr exceeds that limit, the **middle** is elided and replaced with a notice:

```text
<output truncated: 47 832 chars -> 30 000 chars; middle elided>
```

This follows the **pi**/**OpenHands** pattern. The beginning usually contains the command echo and early context, while the end usually contains the final result or error. Eliding the middle preserves both anchors. The limit is configurable via `DOTSY_BASH_MAX_OUTPUT=<chars>`.

#### File Read

The `read` tool enforces:

| Parameter | Default | Notes |
|-----------|---------|-------|
| `limit` | 2 000 lines | Per read call |
| `max_bytes` | 50 000 bytes | Hard cap regardless of line count |
| `offset` | 0 | Enables pagination |

When a file exceeds the limit, the tool returns as many lines as fit and appends:

```text
<truncated: showing lines 1-2000 of 5847; use offset=2000 to continue>
```

The agent can paginate with subsequent `read` calls using the `offset` parameter. Binary files detected by null-byte scan of the first 512 bytes return an error rather than garbled output.

#### Truncation Signal

Every truncating tool uses this standard marker:

```text
<truncated: [reason]; [hint for follow-up]>
```

Examples:

- `<truncated: output exceeded 30 000 chars; DOTSY_BASH_MAX_OUTPUT to raise limit>`
- `<truncated: showing lines 1-2000 of 5847; use offset=2000 to continue>`
- `<truncated: web fetch 1.2 MB exceeded limit; first 100 KB shown>`

Web fetch is capped at **100 KB** of response body. Responses larger than 5 MB return an error rather than a truncated body, with a suggestion to use `curl --output` to save the file locally.

---

