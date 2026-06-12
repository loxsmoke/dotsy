## 13. Session Management

### 13.1 Session Store

One JSONL file per session, modelled on **pi**/**Claude Code**. Files live at:

```
~/.config/dotsy/projects/{encoded-cwd}/{session-uuid}.jsonl
```

The working directory is encoded by replacing path separators with `-`, so `C:\dev\myapp` becomes `-C--dev-myapp`. Each session is a single flat file — no per-session directories, no separate metadata file.

**Per-line schema** (one JSON object per line, append-only):

```json
{
  "uuid": "a1b2c3d4-...",
  "parentUuid": "prev-uuid-or-null",
  "sessionId": "session-uuid",
  "type": "user | assistant | summary",
  "timestamp": "2026-05-20T10:00:00.000Z",
  "cwd": "C:\\dev\\myapp",
  "gitBranch": "main",
  "version": "1.0.0",
  "message": {
    "role": "user | assistant",
    "content": [
      { "type": "text", "text": "..." },
      { "type": "tool_use", "id": "tu_1", "name": "read", "input": { "path": "Foo.cs" } },
      { "type": "tool_result", "tool_use_id": "tu_1", "content": "..." },
      { "type": "thinking", "thinking": "..." }
    ]
  },
  "usage": {
    "inputTokens": 1234,
    "outputTokens": 456,
    "cacheReadTokens": 0,
    "cacheWriteTokens": 0
  }
}
```

The `uuid` / `parentUuid` pair forms a linked list through the session, enabling branching: if a session forks (e.g. after `/undo`), the new branch shares the common prefix but diverges from a different `parentUuid`.

**Compaction summaries** are written as a `type: "summary"` line directly into the JSONL file — no separate `summary.md`. When loading the session, the reader follows `parentUuid` links and uses the most recent summary as the starting context, skipping superseded messages.

All content is stored: user prompts, assistant responses, tool calls (`tool_use` blocks with structured `input`), tool results (`tool_result` blocks), thinking/reasoning blocks, and per-turn token usage.

### 13.2 Session Index

A global index at `~/.config/dotsy/sessions.json` tracks metadata for all sessions:

```json
[
  {
    "sessionId": "...",
    "title": "first user message truncated to 80 chars",
    "cwd": "C:\\dev\\myapp",
    "model": "claude-opus-4-7",
    "createdAt": "2026-05-20T10:00:00Z",
    "updatedAt": "2026-05-20T10:30:00Z",
    "messageCount": 42
  }
]
```

The index is updated at the end of each turn. It is used only for listing — the JSONL file is the authoritative store.

### 13.3 Session Resume

```
dotsy run --resume <session-uuid>
dotsy run --resume          # resumes most recent session for current cwd
```

The loader reads the JSONL file, follows `parentUuid` links to reconstruct the active message chain, and picks up from the last line. If a `summary` entry exists, messages before it are excluded from the LLM context (but remain in the file for audit).

### 13.4 Session List and Cleanup

```
dotsy sessions list                   # tabular list: date, title, cwd, message count
dotsy sessions list --cwd .           # filter by current project
dotsy sessions clean --older-than 30d # delete JSONL files older than N days (default 30)
```

Purge respects a `session.cleanup_days` config key (default 30, minimum 1, 0 = disabled). Cleanup runs at startup on the project directory matching the current cwd. Transcript writing can be disabled via `DOTSY_NO_HISTORY=1`.

### 13.5 Multi-Session

Each `dotsy run` creates a new session UUID and a new JSONL file. Sessions are isolated — concurrent runs write to different files with no shared state.

### 13.6 OpenCode-Compatible Trajectory Export

Dotsy can optionally write a dataset-oriented trajectory artifact modelled on NVIDIA's [`Nemotron-SFT-OpenCode-v1`](https://huggingface.co/datasets/nvidia/Nemotron-SFT-OpenCode-v1), whose OpenCode CLI trajectory rows expose `question_category`, `complexity_level`, `question`, `agent_prompt`, `enabled_tools`, `skills_path`, `uuid`, `messages`, `tools`, `metadata`, and `hf_split` fields. This export is separate from the resumable session JSONL in §13.1: session history optimizes for replay/resume, while trajectory export optimizes for training, evaluation, and offline analysis.

Trajectory logging is disabled by default and controlled only by config:

```toml
[trajectory]
enabled = false
dir     = ".dotsy/trajectories"
```

When `trajectory.enabled = true`, Dotsy writes exactly one UTF-8 JSON file for the entire session after the session ends or is cleanly checkpointed. It must not write Parquet files and must not shard a single session across multiple files. The file path is:

```
{trajectory.dir}/{session-uuid}.json
```

The top-level JSON object uses the same field names as the OpenCode trajectory rows:

```json
{
  "question_category": "software_engineering",
  "complexity_level": "unknown",
  "question": "First user request, verbatim",
  "agent_prompt": "Full initial system prompt sent to the model",
  "enabled_tools": ["read", "grep", "edit", "shell", "todo"],
  "skills_path": ".dotsy/skills",
  "uuid": "session-uuid",
  "messages": [
    { "role": "system", "content": "..." },
    { "role": "user", "content": "..." },
    {
      "role": "assistant",
      "content": "...",
      "tool_calls": [
        {
          "id": "tu_1",
          "type": "function",
          "function": { "name": "read", "arguments": "{\"path\":\"Foo.cs\"}" }
        }
      ]
    },
    { "role": "tool", "tool_call_id": "tu_1", "name": "read", "content": "..." }
  ],
  "tools": [
    {
      "id": "read",
      "description": "Read a file from the workspace.",
      "inputSchema": {
        "jsonSchema": {
          "type": "object",
          "properties": { "path": { "type": "string" } },
          "required": ["path"]
        }
      }
    }
  ],
  "metadata": {
    "uuid": "session-uuid",
    "dotsy_version": "1.0.0",
    "cwd": "C:\\dev\\myapp",
    "git_branch": "main",
    "git_commit": "abc123",
    "model": "claude-opus-4-7",
    "provider": "anthropic",
    "started_at": "2026-05-20T10:00:00.000Z",
    "ended_at": "2026-05-20T10:30:00.000Z",
    "duration_ms": 1800000,
    "token_usage": {
      "input_tokens": 1234,
      "output_tokens": 456,
      "cache_read_tokens": 0,
      "cache_write_tokens": 0
    },
    "outcome": "completed",
    "error": null
  },
  "hf_split": "dotsy"
}
```

Field requirements:

- `question_category`: best-effort category for the first user request. Use `"unknown"` when Dotsy has not classified it; do not omit the field.
- `complexity_level`: best-effort `"beginner" | "intermediate" | "advanced" | "unknown"`. Use `"unknown"` by default.
- `question`: the first user-authored request in the session, verbatim, before any hidden continuation, compaction, or nudge messages.
- `agent_prompt`: the full initial system prompt, including enabled Dotsy instructions, environment block, repo context, and available skill list exactly as sent to the provider.
- `enabled_tools`: tool names available to the model at session start, including built-ins, loaded skills exposed as tools, and MCP tools.
- `skills_path`: the effective skill search path string used for the session, or `""` when skills are disabled/unavailable.
- `uuid`: Dotsy's session UUID. It must match `metadata.uuid`.
- `messages`: the complete provider-facing conversation in OpenAI-compatible chat shape. Preserve system, user, assistant, and tool messages in chronological order; include assistant tool calls and tool outputs; include hidden nudges and compaction summaries only if they were actually sent to the provider.
- `tools`: the full tool schema list available at session start, converted to the OpenCode-compatible `id`, `description`, `inputSchema.jsonSchema` shape.
- `metadata`: Dotsy-specific run metadata. It may contain additional keys, but must include the keys shown above so downstream tooling can filter by repo, model, timing, outcome, and usage.
- `hf_split`: a split label for local dataset preparation. Use `"dotsy"` for normal local exports unless a future batch/export command intentionally sets another split.

Sensitive values must be redacted before writing the trajectory file. API keys, environment variable values, bearer tokens, and approval secrets are replaced with `"[REDACTED]"` in `agent_prompt`, `messages`, tool arguments, tool results, and `metadata`. Redaction happens even when ordinary session logging is enabled.

---

