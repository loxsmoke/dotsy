## 8. Tool System

### 8.1 Interface

```csharp
public interface ITool
{
    string Name { get; }               // PascalCase, e.g. "Read", "Edit", "Shell"
    string Description { get; }        // sent as the tool definition description
    JsonElement InputSchema { get; }   // JSON Schema for the LLM
    ToolSafety Safety { get; }         // ReadOnly | Sequential | Destructive
    bool IsCompletionSignal { get; }   // true only for the "Done" tool
    bool IsWriteTool => false;         // true for Write/Edit/MultiEdit (drives "Allow for project")

    // TUI/approval formatting hooks (default implementations dump raw JSON):
    string FormatRunApproval(JsonElement input, string cwd) => input.GetRawText();
    string FormatPanelArgument(JsonElement input, string cwd) => input.GetRawText();
    string? FormatPanelResult(JsonElement input, string resultContent, string cwd) => null;

    Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext ctx, CancellationToken ct);
}
```

Tool names are **PascalCase** (`Read`, `Write`, `Edit`, `MultiEdit`, `List`, `Grep`, `Glob`,
`FindDefs`, `Shell`, `WebFetch`, `WebSearch`, `Skill`, `Todo`, `Ask`, `Done`, `Task`). They are the
identifiers the model emits in tool calls and the registry dispatches on.

### 8.2 Built-in Tools

#### File Operations

| Tool | Description | Key params |
|------|-------------|-----------|
| `Read` | Read a text file as numbered lines; paginates; binary files (null-byte scan) are rejected | `path`, (`offset?`+`limit?` \| `start_line?`+`end_line?`), `include_diff?` |
| `Write` | Write or overwrite a file; creates parent dirs | `path`, `content` |
| `Edit` | Replace either an exact 1-based inclusive line range **or** a unique `old_string` | `path`, (`start_line`+`end_line` \| `old_string`), `new_string`, `replace_all?` |
| `MultiEdit` | Multiple non-overlapping edits to one file in a single call; all line ranges refer to the file as read (applied bottom-up, so edits never shift each other) | `path`, `edits[]` |
| `List` | List directory contents; marks dirs with `/` | `path`, `recursive?` |

The edit tools are deliberately forgiving toward weaker models:

- **Result echo** — a successful `Edit`/`MultiEdit` returns the edited region(s) of the *resulting*
  file as numbered lines (the same format `Read` produces, with a few context lines, in final
  coordinates), so the model can verify the outcome and reuse the line numbers without re-reading.
- **Replace semantics** — `start_line..end_line` is removed and `new_string` takes its place; the
  tool description tells the model to include the range's original line(s) when it means to insert.
- **Line endings** — replacement lines are normalized to the file's dominant EOL style, and
  `old_string` matching falls back across LF/CRLF conventions when the literal match fails.
- **Lenient arguments, specific errors** — numeric parameters sent as strings (`"start_line":"12"`)
  and `"replace_all":"true"` are accepted (`Read` has the same leniency); missing required
  arguments (`path`, `new_string`) return clear errors instead of raw exceptions; range failures
  are reported distinctly (inverted range vs. past-end-of-file) and in the caller's own coordinate
  dialect (`start_line` vs. 0-based `offset`).

#### Search

| Tool | Description | Key params |
|------|-------------|-----------|
| `Grep` | Ripgrep regex search; respects .gitignore | `pattern`, `path?`, `glob?`, `context_lines?` |
| `Glob` | File-pattern search sorted by mtime | `pattern`, `path?` |
| `FindDefs` | Roslyn: top-level types and members in a directory | `path`, `language?` |

#### Execution

| Tool | Description | Key params |
|------|-------------|-----------|
| `Shell` | Run a shell command; captures combined stdout+stderr; timeout | `command`, `timeout_ms?` |

`Shell` also enforces a hard rail (even under `--yolo`): commands that would discard *all*
uncommitted work in the tree (`git reset --hard`, whole-tree `git checkout`/`git restore`,
`git clean -f`) are blocked with guidance to fix forward or revert a single named file.

#### Web

| Tool | Description | Key params |
|------|-------------|-----------|
| `WebFetch` | Fetch URL as Markdown; HTTPS-upgrades HTTP | `url`, `max_bytes?` |
| `WebSearch` | Web search; returns titles + snippets + URLs | `query`, `num_results?` |

#### Agent Utilities

| Tool | Description | Key params |
|------|-------------|-----------|
| `Skill` | Load a named skill by name | `name` |
| `Todo` | Create/update a structured task list | `items[]` |
| `Ask` | Ask the user a clarifying question with optional choices | `question`, `options?` |
| `Done` | Signal task completion (`IsCompletionSignal = true`) | `summary` |
| `Task` | Delegate to a background subagent (returns task_id) | `prompt`, `tools?` |

#### MCP Passthrough

MCP servers do not add a single `mcp_call` tool. Instead, each tool discovered on a connected
server is registered individually as its own `ITool` (`McpTool`), named `[mcp:<server>]<tool>`, and
appears in the tool list alongside the built-ins.

### 8.3 Tool Registry

`ToolRegistry` holds all registered `ITool` instances. At loop start, the registry serialises all tool definitions to JSON Schema and passes them in `ChatRequest.Tools`. Tools are looked up by `Name` when the model emits a tool call.

External tools (MCP) are registered at session start after MCP server discovery.

### 8.4 Tool Output Size Limits

Tool output limits are part of the tool contract because large results affect model context, provider payload size, and follow-up tool planning. Tools that truncate output must include a machine-readable marker so the model knows the result is incomplete.

#### Shell Output

Shell output is limited to **30 000 characters** (`ShellTool.MaxOutputChars`). If combined stdout + stderr exceeds that limit, the **middle** is elided and replaced with a notice:

```text
<… 17832 characters elided …>
```

This follows the **pi**/**OpenHands** pattern. The beginning usually contains the command echo and early context, while the end usually contains the final result or error. Eliding the middle preserves both anchors. The limit is a compile-time constant (not currently configurable via an environment variable). The default command timeout is 30 000 ms, overridable per call via `timeout_ms`.

#### File Read

The `Read` tool enforces:

| Limit | Value | Notes |
|-------|-------|-------|
| `limit` | 2 000 lines | Per read call (default and maximum) |
| output bytes | 51 200 (50 KB) | Hard cap on the returned text regardless of line count |
| `offset` | 0 | Enables pagination; `start_line`/`end_line` are the 1-based equivalent |

When a file has more lines than the window, the tool returns as many lines as fit and appends:

```text
<truncated: 3847 more lines; use offset=2000 to continue>
```

When the byte cap cuts the output instead, the marker is `<truncated: file has N lines; use offset/limit to read more>`. The agent can paginate with subsequent `Read` calls. Binary files detected by null-byte scan of the first 8 192 bytes return an error rather than garbled output. `Grep` output is capped the same way (50 KB / 2 000 match lines, marker `<truncated: output exceeded limits>`).

#### Truncation Signal

Every truncating tool uses this standard marker:

```text
<truncated: [reason]; [hint for follow-up]>
```

Examples:

- `<… N characters elided …>` (shell middle-elision)
- `<truncated: 3847 more lines; use offset=2000 to continue>` (file read)
- `<truncated: response exceeded 100KB>` (web fetch)

Web fetch is capped at **100 KB** of response body. Responses larger than 5 MB return an error rather than a truncated body, suggesting `curl -s <url> | head -c 102400` to fetch a bounded slice instead.

---

