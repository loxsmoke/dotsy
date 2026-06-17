# Dotsy - Implementation Todo

Tasks are grouped in dependency order: each section can be started once the sections above it are complete.

---

## 1. Project setup ✅

- [x] Create `Dotsy.Core.csproj` (`net9.0`, `Nullable=enable`, `ImplicitUsings=enable`)
- [x] Create `Dotsy.Providers.csproj` with project reference to `Dotsy.Core`
- [x] Create `Dotsy.Mcp.csproj` with project reference to `Dotsy.Core`
- [x] Create `Dotsy.Core.Tests.csproj` (MSTest + Moq, references `Dotsy.Core`)
- [x] Create `Dotsy.Providers.Tests.csproj` (MSTest + Moq, references `Dotsy.Providers`)
- [x] Add all five new projects to `Dotsy.sln`
- [x] Add project reference `Dotsy.Cli` â†’ `Dotsy.Core`, `Dotsy.Providers`, `Dotsy.Mcp`
- [x] Add NuGet packages to `Dotsy.Core`: `Tomlyn`, `LibGit2Sharp`, `Microsoft.CodeAnalysis.CSharp`, `Microsoft.Data.Sqlite`, `Microsoft.Extensions.DependencyInjection`
- [x] Add NuGet packages to `Dotsy.Cli`: `System.CommandLine`, `Spectre.Console`
- [x] Add NuGet packages to test projects: `MSTest.TestFramework`, `MSTest.TestAdapter`, `Moq`

---

## 2. Core abstractions ✅

- [x] `IProvider` interface: `Name`, `GetModelInfoAsync`, `StreamAsync` returning `IAsyncEnumerable<ProviderEvent>`
- [x] `ModelInfo` record: `Id`, `ContextWindow`, `MaxOutputTokens`
- [x] `ProviderEvent` hierarchy: `TextDelta`, `ThinkingDelta`, `ToolCallDelta`, `UsageUpdate`, `StreamEnd`, `StreamError`
- [x] `ChatRequest` record: `ModelId`, `SystemPrompt`, `Messages`, `Tools`, `MaxTokens`, `Temperature`
- [x] `Message` and `ToolDefinition` models; `ToolResultMessage` for tool output
- [x] `ProviderError` hierarchy: `RateLimitError`, `ServerError`, `NetworkError`, `AuthError`, `RequestError`, `ContextLengthError`
- [x] `LoopEvent` hierarchy: `TextChunk`, `ThinkingChunk`, `ToolStarted`, `ToolFinished`, `TurnComplete`, `CompactionOccurred`, `LoopEnded`, `PermissionRequired`
- [x] `LoopContext`: config ref, message history, token budget, session ID, active skills, plan-mode flag
- [x] `ITool` interface: `Name`, `Description`, `InputSchema`, `Safety`, `IsCompletionSignal`, `ExecuteAsync`
- [x] `ToolResult` record: `Content`, `IsError`; `ToolContext` passed to `ExecuteAsync`
- [x] `ToolSafety` enum: `ReadOnly`, `Sequential`, `Destructive`

---

## 3. Configuration system ✅

- [x] Config class tree: `DotsyConfig` with nested `ModelConfig`, `AgentConfig`, `CompactionConfig`, `RetrievalConfig`, `SkillsConfig`, `GitConfig`, `TuiConfig`, `PermissionsConfig`
- [x] `DefaultConfig`: hardcoded baseline values matching spec Â§5.2
- [x] TOML file loading via `Tomlyn`; deserialise into config class tree
- [x] Global config discovery: `~/.config/dotsy/config.toml`
- [x] Project config discovery: walk up from `cwd` to fs root looking for `.dotsy/config.toml`
- [x] Environment variable overlay: map `DOTSY_<SECTION>_<KEY>` onto config properties
- [x] `ConfigLoader.Load()`: merge defaults â†’ global â†’ project â†’ env vars in order
- [x] Secrets rule: API keys only from global config or env vars, never from project config

---

## 4. Anthropic provider ✅

- [x] `AnthropicProvider` implementing `IProvider`
- [x] SSE streaming: parse `content_block_start`, `content_block_delta`, `message_delta` events into `ProviderEvent`
- [x] Tool call streaming: accumulate `input_json_delta` fragments, emit `ToolCallDelta` on `content_block_stop`
- [x] Extended thinking block: emit `ThinkingDelta` from `thinking` content blocks
- [x] `UsageUpdate` from `message_delta.usage` field (input, output, cache_read, cache_creation tokens)
- [x] Auth: `x-api-key` header from config or `ANTHROPIC_API_KEY` env var
- [x] `GetModelInfoAsync`: parse `/v1/models` response; fall back to bundled model list
- [x] Map HTTP / API errors to `ProviderError` subtypes (401â†’`AuthError`, 429â†’`RateLimitError`, 500â†’`ServerError`, context keywordâ†’`ContextLengthError`)
- [x] `Retry-After` header extraction for `RateLimitError.RetryAfter`

---

## 5. OpenAI provider ✅

- [x] `OpenAiProvider` implementing `IProvider`
- [x] SSE streaming: parse `choices[0].delta` events (`content`, `tool_calls` partials, `finish_reason`)
- [x] Tool call streaming: accumulate `tool_calls[n].function.arguments` across deltas, emit `ToolCallDelta` on finish
- [x] `UsageUpdate` from final stream chunk (send `stream_options: {include_usage: true}`)
- [x] Auth: `Authorization: Bearer <api_key>` header
- [x] `GetModelInfoAsync`: parse `/v1/models` response; fall back to bundled model list
- [x] Map HTTP errors to `ProviderError` subtypes (`finish_reason == "length"` â†’ `ContextLengthError`)

---

## 6. Remaining providers ✅

- [x] `AzureOpenAiProvider`: extend `OpenAiProvider`, override endpoint to `{endpoint}/openai/deployments/{deployment}/chat/completions?api-version={api_version}`, use `api-key` header
- [x] `OllamaProvider`: `POST /api/chat` with `stream: true`, parse ndjson response lines, no auth
- [x] `OpenAiCompatibleProvider`: `OpenAiProvider` with configurable `base_url`; used for OpenRouter, Together, DeepSeek, etc.
- [x] `ProviderRegistry`: resolve `IProvider` by name string from config (`anthropic`, `openai`, `azure`, `ollama`, `compatible`)

---

## 7. Retry policy ✅

- [x] `RetryPolicy` class: `MaxRetries=10`, `BaseDelayMs=1000`, `MaxDelayMs=30000`, `Multiplier=2.0`, `JitterFactor=0.2`
- [x] `NextDelay(attempt, serverHint?)`: exponential with Â±20% jitter; `serverHint` overrides calculated delay
- [x] Retry wrapper in all providers: catch `RateLimitError`, `ServerError`, `NetworkError`; do not retry `AuthError` or `RequestError`
- [x] Emit `LoopEvent` retry-countdown ticks during back-off delay so TUI can show "Retrying in Ns Â· attempt x/10"
- [x] Partial stream failure: discard partial response, do not append to history, retry full request

---

## 8. Built-in file tools ✅

- [x] `ReadTool`: read file text; enforce 2000-line / 50KB limits with `offset` pagination; detect binary files (null-byte scan); append `<truncated: â€¦>` notice
- [x] `WriteTool`: write or overwrite file; create parent directories if missing
- [x] `EditTool`: exact `old_string â†’ new_string` replacement; fail if `old_string` not found or not unique; `replace_all` flag
- [x] `MultiEditTool`: apply multiple non-overlapping `old_string â†’ new_string` edits in one call
- [x] `ListTool`: list directory; append `/` to directory entries; `recursive` flag

---

## 9. Built-in search tools ✅

- [x] `GrepTool`: spawn `rg` subprocess; enforce 100 matches / 50KB / 2000-line limits; truncate lines at 500 chars; respect `.gitignore`; `context_lines` param
- [x] `GlobTool`: `Directory.GetFiles` with glob pattern; sort results by `LastWriteTime` descending
- [x] `FindDefinitionsTool`: Roslyn `CSharpSyntaxTree.ParseText`; extract top-level type names and member signatures from directory (max 50 files); return compact outline

---

## 10. Built-in execution and web tools ✅

- [x] `ShellTool`: `Process.Start`; capture combined stdout+stderr; 30 000-char limit with middle elision; configurable `timeout_ms`; `ToolSafety = Destructive`
- [x] `WebFetchTool`: `HttpClient` GET; HTTPS-upgrade HTTP URLs; convert HTML to Markdown; 100 KB body cap; 5 MB â†’ error with curl suggestion
- [x] `WebSearchTool`: submit search query; return titles, snippets, and URLs

---

## 11. Built-in agent utility tools ✅

- [x] `SkillTool`: look up skill by name from `SkillDiscovery`; return `<skill_content>` XML block with Markdown body and companion file paths
- [x] `TodoTool`: create or update a structured task list in `LoopContext`; persist as `<todo>` block in system prompt suffix
- [x] `AskTool`: emit `PermissionRequired` event to pause loop; resume with user's free-text answer
- [x] `DoneTool`: `IsCompletionSignal = true`; carries `summary` string; breaks loop cleanly
- [x] `TaskTool`: launch background `AgentLoop` sub-task; return `task_id`; later tool calls can check status
- [x] Wire `TaskTool.LaunchSubTask` in TUI/headless startup so real sub-agent tasks can launch instead of returning "Sub-task launching is not yet available"
- [x] Add task status/check-result plumbing for launched sub-tasks, including lifecycle cleanup and tests

---

## 12. Tool registry ✅

- [x] `ToolRegistry`: dictionary keyed by tool name; `Register(ITool)` and `TryGetTool(name)`
- [x] `GetToolDefinitions()`: serialise each `ITool.InputSchema` into `ToolDefinition` list for `ChatRequest.Tools`
- [x] Warn to tool log when an MCP tool name collides with a built-in tool name
- [x] Register all built-in tools at session start; MCP tools added after server discovery

---

## 13. Permission system ✅

- [x] Shell command prefix matching against `always_allow` list (`git status`, `git log *`, `dotnet build *`, etc.)
- [x] `PermissionStore`: in-memory session rules + persisted `.dotsy/permissions.json` and `~/.config/dotsy/permissions.json`
- [x] Glob pattern syntax for rules: `{ToolName}({argument})`
- [x] Evaluation order: `deny â†’ allow â†’ ask`; deny always wins
- [x] Hard-coded permanent denials: `Shell(rm -rf /)`, `Shell(rm -rf ~)`, writes outside cwd
- [x] "Always allow" choice writes pattern to `.dotsy/permissions.json`
- [x] `--yolo` / `--dangerously-skip-permissions` flag suppresses all prompts

---

## 14. Agent loop core ✅

- [x] `AgentLoop.RunAsync`: `IAsyncEnumerable<LoopEvent>` driven by `while (!ct.IsCancellationRequested)`
- [x] Turn counter with `MaxTurns` enforcement; yield `LoopEnded(TurnLimitReached)` on breach
- [x] `AppendUserMessage` â†’ `MaybeCompact` â†’ `BuildRequest` â†’ `StreamResponse` â†’ `UpdateTokenUsage` per turn
- [x] Stream provider events and translate to `LoopEvent`s: `TextDeltaâ†’TextChunk`, `ToolCallDeltaâ†’ToolStarted`, etc.
- [x] Consecutive-no-tool nudge: count turns with empty tool calls; after `NudgeLimit` inject nudge message and break
- [x] `ExecuteTools`: look up each tool call by name in `ToolRegistry`; check permissions; run; yield `ToolFinished`
- [x] `AppendToolResults`: append assistant message + all tool results to `LoopContext.Messages`

---

## 15. Parallel tool execution ✅

- [x] Classify each pending tool call's `ToolSafety` from registry
- [x] `ReadOnly` tools: run concurrently via `Task.WhenAll`
- [x] `Sequential` and `Destructive` tools: run one at a time in the order the model returned them
- [x] Merge all results in original tool-call order before appending to message history

---

## 16. Reflection loop ✅

- [x] After each turn that wrote files: if `auto_lint` or `auto_test` is configured, run the command via `ShellTool`
- [x] On non-zero exit code or error output: append failure text as user message; re-enter loop
- [x] `LoopContext.Reflections` counter; abort reflection after `max_reflections` (default 3); reset on clean turn
- [x] Yield `ReflectionOccurred` `LoopEvent` so TUI can show a notice

---

## 17. Session management ✅

- [x] `SessionStore`: append-only JSONL file at `~/.config/dotsy/projects/{encoded-cwd}/{session-uuid}.jsonl`
- [x] Encode cwd: replace path separators with `-`
- [x] Per-line schema: `uuid`, `parentUuid`, `sessionId`, `type` (`user|assistant|summary`), `timestamp`, `cwd`, `gitBranch`, `version`, `message`, `usage`
- [x] After compaction: write a `type: "summary"` line into JSONL; subsequent loader skips messages before it
- [x] `SessionIndex` at `~/.config/dotsy/sessions.json`: write/update entry (`sessionId`, `title`, `cwd`, `model`, `createdAt`, `updatedAt`, `messageCount`) after each turn
- [x] `SessionLoader.Load(uuid)`: follow `parentUuid` links; use most recent summary as starting context
- [x] `DOTSY_NO_HISTORY=1` env var and `--no-history` flag to disable JSONL writing

---

## 18. Session resume and management commands ✅

- [x] `dotsy run --resume <uuid>`: load session and continue
- [x] `dotsy run --resume` (no arg): resume most recent session for current cwd
- [x] `dotsy sessions list`: print tabular list (date, title, cwd, message count); `--cwd .` filter
- [x] `dotsy sessions clean --older-than <days>`: delete JSONL files older than N days (default 30); `session.cleanup_days = 0` disables
- [x] Startup auto-cleanup: on each new session run, purge stale sessions for current cwd

---

## 19. Context window management ✅

- [x] `TokenBudget` record: `ContextWindow`, `ReserveTokens`, `KeepRecentTokens`, `UsedTokens`; computed `Usable`, `UsagePct`, `ShouldCompact`, `ShouldWarn`
- [x] `MaybeCompact`: check `ShouldCompact` before each LLM call; never fire mid-stream
- [x] Compaction algorithm: walk messages from newest; keep `KeepRecentTokens` as "tail" verbatim; submit "head" to LLM for summarisation using structured prompt (Â§11.3)
- [x] Incremental summary update: if a summary already exists, merge new turns into it rather than replacing
- [x] Inject hidden continuation message after compaction: "Context was summarised. Continue naturally."
- [x] Background tool-pair summarisation: after tool call/result pair ages past cutoff, replace with one-sentence summary in background `Task`
- [x] `BuildRequest`: measure non-negotiable block (system + tools + last user msg + pending tool results); throw `ContextTooSmallException` if it alone exceeds `Usable`; prune oldest messages to fit; flatten adjacent same-role messages
- [x] Hard-error recovery: detect `ContextLengthError` per provider; run immediate compaction + retry once; surface error on second failure
- [x] `/compact` slash command: trigger `AgentLoop` compaction manually from the TUI/headless command path and insert the same `CompactionOccurred` divider
- [x] Verify hidden continuation behavior after compaction: ensure the next provider request carries the intended "continue naturally" instruction without exposing it in visible transcript

---

## 20. Environment context injection ✅

- [x] `EnvironmentBlock.Build(SessionContext)`: collect OS, shell, .NET version, cwd, date (rounded to day), git branch, git status (modified + untracked counts)
- [x] Wrap in `<env>` XML tags; place after static system-prompt prefix
- [x] `inject_environment = false` config flag and `DOTSY_INJECT_ENVIRONMENT=0` env var to suppress the block
- [x] `inject_git_status = false` config flag and `DOTSY_INJECT_GIT_STATUS=0` env var to omit git branch/status

---

## 21. System prompt builder ✅

- [x] `SystemPromptBuilder`: compute `_staticPrefix` (identity, principles, tool policy) once in constructor
- [x] `Build(SessionContext)`: append environment block, skills block, project context block, repo map block, optional plan-mode fragment
- [x] `<available_skills>` XML block: list names and one-line descriptions of all discovered skills
- [x] `<repo_map>` block: inject Roslyn-ranked file outlines when `retrieval.repo_map_tokens > 0`
- [x] Plan-mode fragment: inject when `ctx.IsPlanMode = true`
- [x] Tool definitions sent as `ChatRequest.Tools` array, not embedded in system string
- [x] Wire real `AgentLoop` request construction to pass `GitContext`, `SkillDiscovery`, repo map, and added-file context into `SystemPromptBuilder.Build(...)`
- [x] Add `<added_files>` / project-context injection for files collected via `/add <path>` so they affect subsequent model requests

---

## 22. Skills system ✅

- [x] `SkillDiscovery.Scan()`: check paths in order â€” CLI `--skill`, project `.dotsy/skills/`, global `~/.config/dotsy/skills/`, cross-tool, `skills.paths[]` config entries; first-found wins on name collision
- [x] `SkillLoader`: parse YAML frontmatter (`name`, `description`, `allowed-tools`, `disable-model-invocation`) from `SKILL.md`; load Markdown body; list companion files in directory
- [x] `SkillTool.ExecuteAsync`: return `<skill_content name="â€¦">` XML with stripped Markdown body and `<companion_files>` list
- [x] Session permission: first `skill` tool call per skill per session requests approval; subsequent calls auto-allowed
- [x] `/skill <name>` slash command: load skill immediately without model invocation
- [x] `disable-model-invocation: true` frontmatter: skill only loadable via slash command, never auto-selected
- [x] Pass `SkillDiscovery` into the live prompt build so `<available_skills>` is present during normal agent turns

---

## 23. Git integration ✅

- [x] `GitContext`: `LibGit2Sharp.Repository.Discover(cwd)` to detect repo; read `Head.FriendlyName` and short `Head.Tip.Sha`
- [x] Inject `Repository: <path>\nBranch: <name> (<sha>)` into system prompt preamble
- [x] `ReadTool` optional `--include-diff` flag: return `git diff --stat` summary when path is repo root
- [x] `AutoCommit` (when `agent.auto_commit = true`): `git add` affected files after each `edit`/`write`; commit after each turn with message `agent: <first line of assistant reply>`
- [x] Checkpoint refs: write `refs/agent/checkpoints/<session-id>/<turn-n>` after every turn that wrote files
- [x] `/undo` slash command: hard-reset working tree tracked files to the previous checkpoint ref
- [x] Pass `GitContext.TryLoad(cwd)` into live prompt builds so `inject_git_status` actually adds branch/status to the model context
- [x] Auto-commit should use the user's configured git identity when available; fall back to `dotsy <dotsy@localhost>` only when repo/global identity is missing
- [x] Add tests for `agent.auto_commit = true` staging/commit behavior, including dirty-start behavior and no-op clean turns

---

## 24. Code retrieval â€” repo map ✅

- [x] `RipgrepSearch` wrapper: enforce limits (100 matches / 50 KB / 2000 lines / 500-char line clip); append `<truncated>` notice when cut
- [x] `RoslynIndex`: parse `.cs` files with `CSharpSyntaxTree`; extract type + member signatures; cache in SQLite keyed by `(file_path, last_write_utc)`; skip unchanged files on rescan
- [x] File reference graph: nodes = source files; directed edges = "file A references symbol in file B"; weight by reference frequency
- [x] PageRank on graph (power-iteration; inline implementation)
- [x] Personalization vector: boost files mentioned in conversation or user's current message
- [x] Down-weight noise symbols (generated code, auto-properties)
- [x] Render top-ranked files as compact outline up to `repo_map_tokens` budget
- [x] Scale budget 8Ã— when no files are explicitly in context
- [x] Inject as `<repo_map>` block in system prompt; update each turn
- [x] `repo_map_tokens = 0` disables indexing entirely
- [x] Wire `RoslynIndex` + `RepoMap.Build(...)` into the live `AgentLoop` path so repo-map context is generated each turn
- [x] Use current user message and `LoopContext.AddedFiles` as repo-map personalization inputs

---

## 25. MCP support ✅

- [x] `McpClient`: JSON-RPC 2.0 framing; stdio transport (start process, read/write stdin/stdout); HTTP transport (`POST <url>`)
- [x] Session startup: for each `[[mcp.servers]]` entry, start server process or connect to URL; call `tools/list` to fetch manifest
- [x] `McpTool` wrapper: implements `ITool`; forwards `ExecuteAsync` to `tools/call` JSON-RPC call
- [x] Register `McpTool` instances in `ToolRegistry` with `[mcp:<server>]` display prefix
- [x] Tool log prefix shows `[mcp:<server>]` to distinguish external tools
- [x] Server disconnect handling: remove tools from registry, emit warning to conversation panel
- [x] Add config parsing for `[[mcp.servers]]` into `DotsyConfig`
- [x] Start `McpManager` during TUI and headless session startup, register discovered MCP tools, and dispose clients on session exit
- [x] Surface MCP startup/list-tools failures in the TUI/headless logs without aborting unrelated built-in tools

---

## 26. Token usage tracking ✅

- [x] Providers emit `UsageUpdate` events with input, output, cache-read, and cache-write token counts
- [x] `AgentLoop` updates context usage from provider-reported input/output token counts
- [x] `TokenUsageTracker`: receive usage events and accumulate input/output tokens
- [x] Status bar shows model and context usage percentage
- [x] `--output-format json` final object includes `{ result, sessionId, inputTokens, outputTokens, durationMs }`

---

## 27. TUI wiring ✅

- [x] Replace `DemoRunner` dispatch in `AgentWindow.OnInputSubmitted` with real `AgentLoop.RunAsync`
- [x] `StatusBar`: show real session ID (first 8 chars), model name, context-usage percentage from `TokenBudget`
- [x] Context-usage colour: `â‰¤50%` default, `51â€“80%` yellow, `>80%` red
- [x] Wire `LoopEvent` stream: `TextChunk` â†’ append to conversation; `ToolStarted/Finished` â†’ update tool log entry; `PermissionRequired` â†’ show approval overlay; `CompactionOccurred` â†’ insert dim divider line; `LoopEnded` â†’ set status to idle
- [x] Streaming cursor `â–Œ`: append on first `TextChunk` of a turn; remove on `StreamEnd`; blink at 500ms
- [x] Retry countdown: `RetryScheduled` event â†’ update status bar with "â³ Retrying in Ns Â· attempt x/10"
- [x] Slash-command handler: `/clear`, `/model <name>`, `/skill`, `/add <path>`, `/exit`, `/help`
- [x] Tab completion popup for slash commands; `/add` completes file paths from cwd
- [x] `Ctrl+C`: cancel `_scenarioCts` (current loop turn)
- [x] `Ctrl+Q`: exit application
- [x] Changed-files panel: populate `_fileRows` from `LibGit2Sharp` after each `TurnComplete` event
- [x] `AgentLoop.PermissionPrompter` delegate wired for TUI permission gate
- [x] Program.cs `RunTui()` populates `TuiSessionContext` with all dependencies

---

## 28. CLI commands and DI wiring ✅

- [x] `System.CommandLine` root command `dotsy` with global options (`--model`, `--provider`, `--max-turns`)
- [x] `dotsy run` command: launch TUI session; bind `--resume`, `--bare`, `--no-history`, `--yolo`, `-p`, `-f`, `--output-format`
- [x] `dotsy sessions list [--cwd]` and `dotsy sessions clean [--older-than <days>]`
- [x] `dotsy config show`
- [x] `dotsy skills list`: print all discovered skills with names and descriptions
- [x] Apply CLI flag overlay on top of `ConfigLoader.Load()` result

---

## 29. Headless and non-interactive mode ✅

- [x] TTY detection: `Console.IsInputRedirected` â†’ skip TUI, use headless output path
- [x] `-p "prompt"` flag: single-shot run; print result to stdout and exit
- [x] `-f <file>` flag: read prompt from file, same single-shot behaviour
- [x] `--output-format text`: final assistant response to stdout
- [x] `--output-format json`: emit one JSON object on exit: `{ result, sessionId, inputTokens, outputTokens, durationMs }`
- [x] `--output-format stream-json`: newline-delimited JSON per `LoopEvent` record
- [x] Exit codes: `0` success, `1` loop failure, `2` config/auth error, `4` context limit exceeded, `130` `Ctrl+C`
- [x] `--bare` flag: skip loading project config
- [x] Headless approval: `Destructive` tool handled by PermissionStore

---

## 30. Tests ✅

- [x] `AgentLoop`: nudge triggers after `NudgeLimit` consecutive no-tool turns
- [x] `AgentLoop`: `MaxTurns` enforcement yields `LoopEnded(TurnLimitReached)`
- [x] `AgentLoop`: `DoneTool` call breaks loop with `LoopEnded(TaskComplete)`
- [x] `BuildRequest`: non-negotiable block too large throws `ContextTooSmallException`
- [x] `BuildRequest`: prunes oldest messages first to stay within `Usable` budget
- [x] `PermissionStore`: deny rule always wins over allow rule; glob pattern matching
- [x] `TokenBudget`: `ShouldCompact` and `ShouldWarn` thresholds
- [x] Compaction: messages before cut point are summarised; tail messages kept verbatim
- [x] `AnthropicProvider`: parse SSE fixture for text delta, tool call accumulation, usage update, thinking block
- [x] `OpenAiProvider`: parse SSE fixture for delta accumulation, finish reason, usage chunk
- [x] `RetryPolicy.NextDelay`: jitter stays within Â±20%; `serverHint` overrides; caps at `MaxDelayMs`
- [x] `EditTool`: fails when `old_string` appears zero times; fails when it appears more than once without `replace_all`
- [x] `GrepTool`: output is capped at 100 matches and truncation notice is appended

---

## 31. OpenCode-compatible trajectory export ✅

- [x] Add `TrajectoryConfig` to `DotsyConfig` with `enabled = false` and `dir = ".dotsy/trajectories"` defaults
- [x] Add `[trajectory]` TOML parsing, `DOTSY_TRAJECTORY_ENABLED` / `DOTSY_TRAJECTORY_DIR` env overlays, and `/config` catalogue entries for the new keys
- [x] Define trajectory DTOs in `Dotsy.Core.Session` matching spec Â§13.6 fields: `question_category`, `complexity_level`, `question`, `agent_prompt`, `enabled_tools`, `skills_path`, `uuid`, `messages`, `tools`, `metadata`, `hf_split`
- [x] Implement conversion from Dotsy's `Message` / `ContentBlock` model to OpenAI-compatible chat messages, preserving assistant tool calls, tool results, hidden nudges, and provider-facing compaction summaries
- [x] Implement conversion from `ToolDefinition` to OpenCode-compatible tool rows with `id`, `description`, and `inputSchema.jsonSchema`
- [x] Capture the initial provider-facing `agent_prompt`, initial tool list, effective skill paths, first user request, start/end timestamps, provider/model, cwd, git branch/commit, final outcome, and accumulated token usage for each session
- [x] Implement `TrajectoryExporter` that writes one UTF-8 `.json` file per whole session to `{trajectory.dir}/{session-uuid}.json`; do not write Parquet files and do not shard sessions
- [x] Add secret redaction before write for API keys, bearer tokens, configured secret values, environment variable values, tool arguments, tool results, messages, `agent_prompt`, and metadata
- [x] Wire trajectory export into both TUI and headless session lifecycle so clean exits, completion-tool exits, turn-limit exits, cancellations, and loop errors produce an artifact when `trajectory.enabled = true`
- [x] Keep trajectory export independent from normal session history: `--no-history` / `DOTSY_NO_HISTORY=1` should disable JSONL history only, not trajectory export unless `trajectory.enabled = false`
- [x] Add failure handling: export errors should surface as warnings in TUI/headless logs without masking the agent's original exit code
- [x] Add unit tests for config loading/defaults/env overrides and config editor visibility
- [x] Add unit tests for message/tool-schema conversion, including assistant tool calls and tool result ordering
- [x] Add unit tests for redaction to ensure secrets are replaced with `[REDACTED]` throughout nested trajectory payloads
- [x] Add integration tests for disabled-by-default behavior, enabled one-file JSON output, no Parquet artifacts, and behavior when normal session history is disabled

---

## 32. Bug fixes and polish
- [x] When text is entered on the bottom command line and it becomes longer than the screen width then wrap words to the next line. As text length increases and new lines are added  expand the field vertically up to the limit of the field. Then more lines hould not expand the field but text shold be navigable via arros.
- [x] When text in the bottom command line is deleted and the number of the lines decreases then shrink the field so that it is not higher than the number of lines of wrapped text.
- [x] Todo tool in tools panel should show different format. If working only on one task show the task name. If working on more than one task show "Todo  N tasks. Task1, Task2, Task3...". Here N is the numer of tasks in the list and Task1 Task2 Task3 are task descriptions. Show up to 3 descriptions and if more than append ... after 3 descriptions
- [x] When tool on the tools panel is clicked then show the tool output but do not show the commad line and do not allow text input. This panel should only show the command text, output, and should be able to navigate withing the text so tha entire output could be seen
- [x] Add /resume command to continue the last session after dotsy restart
- [x] Wrap text in the conversation panel. If window changes size then text should be wrapped to fit the new window size.
- [x] When thinking is in progress show running progress by changing the capitalization of "running" text at the top of the screen. Letters should change to uppercase from left to right and when text if all uppercase then start changing to lowercase from left to right. 
- [x] When showing inspection of the edit tool show the full input after the current output. Show what parameters were passed to the tool (search for text or line numbers) and what text was searched for and what text had to be added.
- [x] Dotsy should not exist when Escape key is pressed when tool asks for confirmation. Just return focus to the panel that is asking to confirm or deny.
- [x] Find all usages of code similar to this (where input is JsonElement): `input.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";` Introduce static class with extension method that accepts JsonElement and property name and returns string as this code. Create new folder Utils for this new class in Dotsy.Core project and use proper namespace for it.
- [x] Word wrapping in the convo panel changes color of text segments in the subsequent lines for no reason. For example line 1 has positions 2 and 3 highlighted which is as expected. Line two of wrapped text also has columns 2 and 3 highlighted for absolutely no reason. 
- [x] Improve write tool approval process. Add "Allow for project" after allow once selection. This new selection means that all write requests are automatically approved when writing to the project folder or subfolders. The only exception is if tool tries to write into .dotsy folder. Then write tool should always ask for permission to write.
- [x] Add scrollbars that show up on the right side of the panels of Conversation and Tools.
- [x] Store timestamp when tool was run. Show that timestamp on the inspection panel after the duration of the tool run.  For example now time shown is (1s) with timestamp this should appear as (1s ISO datetime in the local timezone)
- [x] Use ctrl-q to quit instead of ctrl-d
- [x] Fix the bug so that glob command would work correctly. Here is failed run: "Glob **/*.cs" wutg erroro that filename, directory name or volume label syntax is incorrect.
- [x] For scrollbars use ░ and █ characters instead of vertical bar and # character.
- [x] For vertical scrollbars add ▲ at the top and ▼ at the bottom of the scroll bar. This effecivelly reduces the height by 2 so adjust calculations accordingly. If height for the scroll bar shrinks to 2 then show just these two characters. If scrollbar shfinks to height 1 then show only scrollbar background character.
- [x] Add horizontal scrollbar to the inspection panel if contents is longer than the screen width. Also add vertical scrollbar if content is taller than panel width. Put scrollbars on the frame and not inside of the panel. For horizontal scrollbar use different ending characters: ◄ left size and ► right side. Use use ░ and █ for scrollbar background and thumbtack.
- [x] When lines are longer than viewable area in the inspection panel howizontal scrollbar does not appear
- [x] Text highlighting with shift-arrow keys does not work in comman entry field.
- [x] Copy to clipboard does not work either using ctrl-C or ctrl-insert keys
- [x] FindDefinitions tool in the tools panel shows raw JSON input (e.g. `✓  FindDefinitions{"path":"ThemeManager.cs"}   0s`). Change it to a readable format similar to what the Read and Grep tools use in the tools panel.
- [x] Text selection with shift+arrow keys does not select anything. Also, when the entire text is selected via ctrl-a or part is selected with the mouse, pressing arrow keys does not clear the selection.

---

## 33. Configurable convo/tools panel split

- [x] Make convo and tools panel split percentage configurable. Call it left-panel-width-percentage (originally shipped misspelled as `left-poanel-width-percentage`; renamed later with the legacy key still read). Use current value from the code but store this percentage in config file.
- [x] Add dynamic resizing of the split. Alt-left and alt-right keys should expand or contract the panel split. Alt-right expands convo and alt-left contracts convo panel, whether focus is on convo or tools. Do not allow tools or convo to be less than minimum width which we can set to 20 characters.
- [x] Dynamic resizing should save the percentage in the config file. 

---

## 34. `/self` command ✅

- [x] Add `/self` and `/self <question>` to the shared slash-command catalog, help output, and tab completion.
- [x] Build a `SelfContextBuilder` service that collects current Dotsy runtime, session, provider/model, cwd/git/project state, config values, slash-command catalog, registered tools, system details, and GPU details when available.
- [x] Format the generated self-context as Markdown, with stable headings and compact tables/lists instead of XML, so the injected context is readable in the conversation and useful to the model.
- [x] Redact secret-bearing config values before they enter the Markdown context, using `set:redacted`, `placeholder`, or `not set` states while preserving source metadata when available.
- [x] Keep filesystem, git, GPU, and memory probes off the UI thread with short timeouts; represent unavailable or unsupported probes as `unknown` without blocking command handling.
- [x] Cap the generated Markdown prompt to existing tool-output-style limits, summarizing long sections and excluding full config files, diffs, environment variable values, and arbitrary command output.
- [x] When `/self` has no arguments, inject the Markdown self-context plus a default question asking for a concise runtime/configuration summary into the normal agent loop.
- [x] When `/self <question>` is entered, inject the Markdown self-context plus the user question into the normal agent loop without printing raw diagnostics directly.
- [x] Persist the injected `/self` user prompt consistently with normal session history and ensure trajectory export captures the generated context with normal redaction.
- [x] Add unit tests for Markdown context generation, secret redaction states, command/tool catalogue inclusion, timeout fallback behavior, and prompt-size capping.
- [x] Add TUI integration tests or focused handler tests proving `/self` routes through the normal agent loop and `/self <question>` preserves the question text.

---

## 35. `/sec` command ✅

- [x] Add `/sec` to the shared slash-command catalog, help output, and tab completion.
- [x] Implement a security summary renderer that reads effective state from `PermissionStore`, current config, CLI/session flags, and any transient TUI approvals.
- [x] Show current mode: normal prompts, `--yolo` / skip-prompts mode, headless no-prompt mode, and any project-write approval state.
- [x] Show rule sources, including configured `permissions.always_allow`, configured `permissions.never_allow`, global permissions file, project `.dotsy/permissions.json`, and session-only approvals/denials.
- [x] Show tool default behavior for read-only tools, write/edit tools, shell, task/subagent, skills, and MCP tools, clearly distinguishing `allow`, `ask`, and `no access`.
- [x] Show path-oriented effective access for cwd, `.dotsy/`, files added with `/add`, approved project folders, and paths outside cwd.
- [x] Show effective rules in evaluation order, with hard denials and configured/session denies before allow rules because deny wins.
- [x] Include recent session decisions when available, including allow once, always allow, allow for project, and deny decisions.
- [x] Keep `/sec` display-only; it must not mutate permissions, write files, request approval, or start an agent turn.
- [x] For permission categories that cannot yet be represented precisely, print `in progress` or `not yet detailed` instead of implying access is broader than known.
- [x] Add unit tests for summary rendering, rule ordering, yolo/headless modes, project-write approval, `.dotsy` write prompting, outside-cwd denial, and unknown/in-progress categories.

---

## 36. Color themes

- [x] Theme infrastructure: make `Palette` a selectable attribute set resolved from `[tui].theme` at startup; validate the value (`dark | light | system | borland`), fall back to `dark` with a startup warning on unknown names, and ensure every view reads colors only through `Palette` (no hardcoded attributes left in views). Live re-theme via `/config tui.theme <name>` should repaint without restart. (Chrome + new output recolor live; existing scrollback keeps baked cell colors until `/clear`.)
- [x] `dark` theme: extract the current hardcoded VS Code Dark+ palette as the named `dark` theme and keep it the default; verify no visual regressions after the indirection.
- [x] `light` theme: dark-text-on-light palette per spec §3.12.1; tune contrast for status bar, diff add/delete backgrounds, selection rows, and syntax colors on real light terminals. (Colors set; real-terminal contrast tuning still advisable.)
- [x] `system` theme: detect the terminal/system light-dark preference at startup and resolve to `dark` or `light`; fall back to `dark` when detection is unsupported and report the resolved theme in `/self` context. (Detection via `COLORFGBG`; Windows Terminal has no signal → falls back to dark.)
- [x] `borland` theme: Turbo Pascal 7 IDE palette per spec §3.12.1 (blue background, gray/white text, gray dialog chrome, black-on-gray status bar); tune diff, syntax, and scrollbar colors against the blue background. (Colors set; real-terminal tuning still advisable.)
