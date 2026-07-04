## 5. Configuration System

### 5.1 Resolution Order (later overrides earlier)

1. Built-in defaults (hardcoded in `DefaultConfig`)
2. Global config: `~/.config/dotsy/config.toml`
3. Project config: `.dotsy/config.toml` (walked up from cwd to fs root)
4. Environment variables: `DOTSY_<SECTION>_<KEY>` (e.g. `DOTSY_MODEL_ANTHROPIC_ID`)
5. CLI flags: `--provider`, `--model`, `--max-turns`

Project configs do not load `api_key` values; secrets only load from the global config or environment variables.

### 5.2 Config Schema (TOML)

```toml
[model]
provider = "anthropic"                 # anthropic | openai | azure_openai | ollama | compatible | gemini
max_output_tokens_per_request = 8192

[model.anthropic]
id      = ""                           # e.g. claude-sonnet-4-6
api_key = ""                           # or env ANTHROPIC_API_KEY

[model.openai]
id       = ""                          # e.g. gpt-4o
api_key  = ""                          # or env OPENAI_API_KEY
base_url = "https://api.openai.com/v1"

[model.azure]
id          = ""
api_key     = ""                       # or env AZURE_OPENAI_API_KEY
endpoint    = ""
deployment  = ""
api_version = "2025-01-01"

[model.ollama]
id                 = ""                # e.g. llama3
base_url           = "http://localhost:11434"
max_context_tokens = 131072            # sent as num_ctx on chat calls

[model.compatible]
id       = ""
api_key  = ""                          # also falls back to env OPENAI_API_KEY
base_url = ""

[model.gemini]
id      = ""                           # e.g. gemini-2.5-flash-lite
api_key = ""                           # or env GEMINI_API_KEY

[agent]
max_steps            = 0               # currently reserved; 0 = unlimited
max_turns            = 1000            # hard ceiling; 0 = unlimited
parallel_tools       = true            # execute eligible tools concurrently
auto_commit          = false           # git auto-commit after file edits
nudge_limit          = 3               # max consecutive non-terminal text-only turns before stopping (headless)
interactive_nudge_limit = 3            # nudge limit used by the TUI (1 = yield after one stalled turn)
auto_continue_on_nudge = true          # on a recoverable nudge, inject a hint and retry instead of stopping
auto_continue_max_attempts = 3         # max progress-guarded auto-continue retries before giving up
auto_continue_on_end_turn_intent = true # retry when a text-only turn announces the next step but calls no tool
repeat_window_turns  = 8               # rolling window for repeated tool-call detection
repeat_threshold     = 3               # 0 disables repeated tool-call nudging
auto_lint            = false           # run dotnet build after writes and reflect on errors
auto_test            = false           # run dotnet test after writes and reflect on failures
max_reflections      = 3               # max lint/test reflection cycles per turn
inject_environment   = true            # include <env> block in requests
inject_git_status    = true            # include git branch/status in the env block

[compaction]
enabled              = true
threshold_pct        = 0.80            # proactive summarisation trigger
reserve_tokens       = 16384           # buffer subtracted from context window
keep_recent_tokens   = 20000           # recent tokens to preserve verbatim after compaction
tool_pair_summarize  = true            # collapse old tool call/result pairs into short notes

[retrieval]
repo_map_tokens      = 1024            # repo-map budget; 0 = disabled
ripgrep_max_matches = 100
ripgrep_max_bytes   = 51200

[skills]
paths      = []                         # additional skill directories
cross_tool = true                       # also scan cross-tool skill directories

[[mcp.servers]]
name      = "filesystem"
transport = "stdio"                    # stdio | http; defaults to stdio
command   = "npx"
args      = ["-y", "@modelcontextprotocol/server-filesystem", "/path"]

[[mcp.servers]]
name      = "remote"
transport = "http"                     # inferred as http when url is set
url       = "http://localhost:3000/mcp"

[git]
auto_stage = false                      # stage changed files automatically
diff_context_lines = 3

[tui]
left-panel-width-percentage = 70
verbose = false                         # show tool calls/results inline in the conversation panel
theme = "dark"                          # dark | light | system | borland

[permissions]
always_allow = [
  "Shell(git status)",
  "Shell(git log *)",
  "Shell(git diff *)",
  "Shell(dotnet build *)",
  "Shell(dotnet test *)",
  "Shell(dotnet run *)",
  "Shell(ls *)",
  "Shell(dir *)",
]
never_allow = []

[session]
cleanup_days = 30                       # 0 disables startup cleanup
log_enabled  = true
log_dir      = ".dotsy/sessions"

[trajectory]
enabled = false                         # write OpenCode-compatible trajectory export
dir     = ".dotsy/trajectories"         # one JSON file per completed Dotsy session
```

### 5.3 Environment Variables

Most scalar settings can be overridden with `DOTSY_<SECTION>_<KEY>`, using underscores for nested sections and snake-case keys:

- `DOTSY_MODEL_PROVIDER=gemini`
- `DOTSY_MODEL_MAX_OUTPUT_TOKENS_PER_REQUEST=4096`
- `DOTSY_MODEL_OLLAMA_MAX_CONTEXT_TOKENS=131072`
- `DOTSY_AGENT_NUDGE_LIMIT=7`
- `DOTSY_AGENT_INJECT_ENVIRONMENT=false`
- `DOTSY_TUI_THEME=borland`
- `DOTSY_SESSION_LOG_ENABLED=false`
- `DOTSY_TRAJECTORY_ENABLED=true`

Provider API keys also support conventional provider variables:

- `ANTHROPIC_API_KEY`
- `OPENAI_API_KEY`
- `AZURE_OPENAI_API_KEY`
- `GEMINI_API_KEY`

Array/table settings such as `skills.paths`, `mcp.servers`, and permission rule lists are loaded from TOML, not from the generic scalar environment overlay.

Runtime-only environment variables outside the config object include `DOTSY_NO_HISTORY=1` for disabling session writes and `DOTSY_RIPGREP_PATH` for forcing a ripgrep binary path.

### 5.4 `/config`

The TUI `/config` command can display active values, list commonly edited keys, and persist scalar values:

```text
/config
/config list
/config model.provider anthropic
/config model.anthropic.id claude-sonnet-4-6
/config tui.theme borland
```

When a project config exists, non-secret changes are written there. `api_key` changes are always written to the global config.

### 5.5 Secrets

API keys are never written to `.dotsy/config.toml`. The global config (`~/.config/dotsy/config.toml`) is the right home. Alternatively, environment variables are preferred in CI.

---
