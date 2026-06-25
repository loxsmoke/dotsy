## 16. Permission and Tool Approval

Dotsy's permission system is centered on `PermissionStore`. The agent loop asks the store for an effective verdict before running non-read-only tools, and `/sec` renders a human-readable snapshot of the same state. The model should never infer permissions from tool descriptions alone; the loop is the enforcement point.

### 16.1 Tool Safety Classification

Every `ITool` declares a `ToolSafety` value. Safety drives parallel execution decisions (see section 7.4) and determines whether an `Ask` verdict needs a user approval prompt.

| Safety level | Examples | Default execution behavior |
|---|---|---|
| `ReadOnly` | `read`, `glob`, `grep`, `list`, `find_definitions`, `todo`, `ask`, `done` | Allowed without prompting. |
| `Sequential` | `edit`, `multi_edit`, `webfetch`, `websearch`, `task`, MCP tools with sequential safety | Evaluated through `PermissionStore`; prompt when verdict is `Ask`. |
| `Destructive` | `write`, `shell` | Evaluated through `PermissionStore`; prompt when verdict is `Ask`, deny when verdict is `Deny`. |

Tool grouping in `/sec` is more user-oriented than the raw enum:

| Group | Tools | Displayed behavior |
|---|---|---|
| read-only | Read-only built-ins | `allow` |
| write | `Write`, `Edit`, `MultiEdit` | `ask in cwd; no access outside cwd; .dotsy asks even with project approval` |
| shell | `Shell` | `ask unless allowed or denied by rule` |
| task subagent | `Task` | `ask` |
| skills | `Skill` | `allow as read-only tool; skill-specific permission details not yet detailed` |
| mcp | Tools not in the built-in registry | Depends on exposed tool safety; external server trust is not yet detailed |

When `--yolo` / `--dangerously-skip-permissions` is active, the displayed behavior becomes `allow` for all categories because `PermissionStore.Evaluate` returns `Allow` immediately.

### 16.2 Permission State Sources

`PermissionStore` combines configured rules, persisted rules, session decisions, path checks, and mode flags:

| Source | Lifetime | Storage |
|---|---|---|
| Config allow rules | Loaded at startup | `[permissions].always_allow` |
| Config deny rules | Loaded at startup | `[permissions].never_allow` |
| Global persisted rules | Across projects | `~/.config/dotsy/permissions.json` |
| Project persisted rules | Current project | `.dotsy/permissions.json` |
| Session allow/deny decisions | Current process | In memory |
| Project write approval | Current process | In memory |
| Hard denials | Built-in | Code |
| Yolo mode | Current process | CLI flag |

Persisted permission files use this JSON shape:

```json
{
  "always_allow": ["Shell({\"command\":\"dotnet build *\"})"],
  "never_allow": ["Shell({\"command\":\"format *\"})"]
}
```

The legacy conceptual form remains `{ToolName}({argument})`: rules are glob patterns matched against the exact tool name and serialized argument string.

### 16.3 Evaluation Order

`PermissionStore.Evaluate(toolName, argument)` returns one of `Allow`, `Deny`, or `Ask`.

Evaluation order is:

1. If `Yolo` is true, return `Allow`.
2. Apply hard denials.
3. Deny write tools outside the current working directory.
4. Apply configured and session deny rules.
5. Apply configured and session allow rules.
6. Apply project write approval for `Write`, `Edit`, and `MultiEdit` inside the project, except `.dotsy`.
7. Otherwise return `Ask`.

Deny rules win over allow rules. Hard denials and outside-cwd write denials cannot be overridden by config, persisted rules, or session approvals.

Hard denials include:

- `Shell(rm -rf /)`
- `Shell(rm -rf ~)`
- `Shell(rm -rf *)`
- `Shell(format *)`
- `Shell(del /f /s /q *)`
- `Write`, `Edit`, and `MultiEdit` outside the current working directory

### 16.4 Path-Sensitive Write Permissions

Write-like tools are path-sensitive. The store extracts `path` from JSON arguments when present; otherwise it treats the whole argument as the path.

| Target | Normal mode | After `Allow for project` | Yolo mode |
|---|---|---|---|
| File inside cwd | `Ask` unless allowed by rule | `Allow` | `Allow` |
| File inside `.dotsy` | `Ask` | `Ask` | `Allow` |
| File outside cwd | `Deny` | `Deny` | `Allow` |
| File added through `/add` | Read-only context; write follows normal path rules | Same path rules | `Allow` |

The `.dotsy` exception exists so project-write approval does not silently modify Dotsy's own config, permissions, or session files.

### 16.5 Approval Workflow

When a non-read-only tool receives an `Ask` verdict, the agent loop yields a `PermissionRequired` event and waits for the UI or headless policy to resolve it.

```csharp
public enum PermissionDecision
{
    AllowOnce,
    AllowForProject,
    AlwaysAllow,
    Deny
}
```

Approval choices map to store mutations:

| Choice | Effect |
|---|---|
| `AllowOnce` | Adds a session allow rule for the exact tool call. |
| `AlwaysAllow` | Adds a session allow rule and persists it to `.dotsy/permissions.json`. |
| `AllowForProject` | Allows `Write`, `Edit`, and `MultiEdit` inside the project for the current process, excluding `.dotsy`. |
| `Deny` | Adds a session deny rule and returns a permission-denied tool result. |

The TUI renders these choices in the tool-approval overlay. The loop does not auto-retry after a denial; the model receives an error result and may choose another approach.

In headless mode, a tool that still needs approval cannot block for UI input. It fails with the headless permission-denied path described in section 18 unless a matching allow rule or yolo mode permits it.

### 16.6 Effective Rules and Recent Decisions

`/sec` shows effective rules in the same order used for evaluation:

1. Hard denials.
2. Outside-cwd write denial.
3. Config and session deny rules.
4. Config and session allow rules.
5. Project write approval, when active.
6. Fallback `ask` for non-read-only tools without a matching rule.

Recent decisions are tracked in memory as `PermissionDecisionRecord` entries with:

| Field | Meaning |
|---|---|
| `Kind` | `allow once`, `always allow`, `allow for project`, or `deny` |
| `Rule` | Human-readable summary of the affected rule or project-write grant |
| `Timestamp` | Local decision time |

`/sec` summarizes recent decisions without printing raw JSON arguments or large payloads. JSON arguments are reduced to important fields such as shell `command`, write `path`, web `url`, search `query`, skill `name`, or task identity.

### 16.7 `/sec` Security Summary Contract

`/sec` is display-only. It must not mutate permission state, write permission files, ask for approval, or start an agent turn.

The summary is organized as:

| Section | Contents |
|---|---|
| Mode | Whether prompts are enabled, yolo state, headless state, and project-write approval state. |
| Rule sources | Counts for configured allow/deny rules, global and project permission-file paths, and loaded session/global/project entries. |
| Path access | Effective write verdicts for cwd, `.dotsy`, files added with `/add`, and a representative outside-cwd path. |
| Tool permissions | Registered tools grouped into read-only, write, shell, task subagent, skills, and MCP categories. |
| Effective rules | Ordered deny/allow/ask rules, including hard denials and project-write approval. |
| Recent decisions | Session-scoped approval/denial history for this process. |
| Notes | Explicit limitations and in-progress categories. |

The renderer keeps output plain text and LF-normalized so the TUI can print it directly. It avoids raw rule punctuation, large JSON payloads, and non-ASCII display artifacts.

### 16.8 Known Gaps

The current model intentionally reports some areas as `in progress` or `not yet detailed` instead of implying broader guarantees:

- MCP server trust and remote-tool provenance.
- Skill body approval history beyond the current skill tool behavior.
- Nested prompts from subagents or external tools.
- Fine-grained read access controls; read-only built-in tools are currently allowed.

These gaps should remain visible in `/sec` until the permission model exposes exact state for them.

---
