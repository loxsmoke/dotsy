## 5. Configuration System

### 5.1 Resolution Order (later overrides earlier)

1. Built-in defaults (hardcoded in `DefaultConfig`)
2. Global config: `~/.config/dotsy/config.toml`
3. Project config: `.dotsy/config.toml` (walked up from cwd to fs root)
4. Environment variables: `DOTSY_<SECTION>_<KEY>` (e.g. `DOTSY_MODEL_ANTHROPIC_ID`)
5. CLI flags: `--model`, `--provider`, `--max-output-tokens-per-request`, etc.

### 5.2 Config Schema (TOML)

```toml
[model]
provider = "anthropic"          # anthropic | openai | azure | ollama | openrouter | compatible
max_output_tokens_per_request = 8192 # max output tokens per request

[model.anthropic]
id       = ""                   # e.g. claude-opus-4-7
api_key  = ""                   # or env ANTHROPIC_API_KEY

[model.openai]
id       = ""                   # e.g. gpt-4o
api_key  = ""
base_url = "https://api.openai.com/v1"

[model.azure]
id           = ""
api_key      = ""
endpoint     = ""
deployment   = ""
api_version  = "2025-01-01"

[model.ollama]
id       = ""                   # e.g. llama3
base_url = "http://localhost:11434"

[agent]
max_steps     = 0               # 0 = unlimited (like opencode)
max_turns     = 1000            # hard ceiling (like goose)
parallel_tools = true           # execute independent tool calls concurrently
auto_commit   = false           # git auto-commit after file edits (like aider)
nudge_limit   = 3               # abort after N consecutive turns with no tool use

[compaction]
enabled              = true
threshold_pct        = 0.80     # proactive summarisation trigger (80% like goose)
reserve_tokens       = 16384    # buffer subtracted from context window
keep_recent_tokens   = 20000    # tokens to preserve verbatim after cut
tool_pair_summarise  = true     # background summarisation of verbose tool outputs

[retrieval]
repo_map_tokens   = 1024        # aider-style map budget; 0 = disabled
ripgrep_max_matches = 100
ripgrep_max_bytes   = 51200     # 50 KB

[skills]
paths = []                      # additional skill directories
cross_tool = true               # also scan ~/.agents/skills/, ~/.claude/skills/

[git]
auto_stage = false              # stage changed files automatically
diff_context_lines = 3

[tui]
left-panel-width-percentage = 70
theme = "dark"                  # dark | light | system | borland

[trajectory]
enabled = false                  # write OpenCode-compatible trajectory export
dir     = ".dotsy/trajectories"  # one JSON file per completed Dotsy session
```

### 5.3 Secrets

API keys are never written to `.dotsy/config.toml`. The global config (`~/.config/dotsy/config.toml`) is the right home. Alternatively, environment variables are preferred in CI.

---

