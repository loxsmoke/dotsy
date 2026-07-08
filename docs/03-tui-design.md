## 3. TUI Design

### 3.1 Layout

```
┌─────────────────────────────────────────────────────────────────────┐
│  dotsy  ·  session: abc123  ·  claude-opus-4-7  ·  [75% ctx]  idle  │  ← status bar (row 0)
├──────────────────────────────────────────────────┬──────────────────┤
│                                                  │ Tools            │
│  Conversation panel                              │ ✓  Read  Foo.cs  │
│  (scrollable; streams assistant text live)       │ ✓  Grep  IFoo…   │
│                                                  │ ◌  Edit  Foo.cs  │
│  User › implement IFooService                    │                  │
│                                                  │                  │
│  Agent › I'll start by reading the interface...  │                  │
│  ▌ (streaming cursor)                            │                  │
│ ┌ changed files (2, +1 -0) ──────────────────────│                  │
│ │  + src/Models/Bar.cs                           │                  │
│ │  ↳ src/Foo.cs   +12  -3                        │                  │
├──────────────────────────────────────────────────┴──────────────────┤
│  > _                                                                 │  ← input bar (last row)
└─────────────────────────────────────────────────────────────────────┘
```

- **Status bar** (`Height=1`, pinned to `Y=0`): session ID, active model, context-usage percentage, spinner, and current state. Format: `  dotsy  ·  {session-id}  ·  {model}  ·  [{ctx%} ctx]  {spinner} {state}`.
- **Conversation panel** (left, default 70% width): streams assistant text in real time. User turns prefixed `User ›` (cyan), assistant turns `Agent ›`, extended-thinking text `think ›` (dim). Markdown rendered inline (see §3.4).
- **Pane divider**: an explicit 1-column `PaneDivider` view between the conversation and tools panels, drawing `┬`, `│`, and `┴` so the shared border junctions stay correct.
- **Tool log panel** (right, remaining width): one row per tool call showing status icon, name, argument, and elapsed seconds.
- **Changed files panel**: a frame docked to the bottom of the conversation panel, hidden until a turn writes files. Title shows count and add/delete breakdown: ` changed files (N, +A -D) `. Height grows with the file count, capped at 30% of the conversation panel height. `Enter` on a row opens the file's diff in the inspection overlay (§3.8).
- **Input bar** (bottom): `> ` prompt plus a word-wrapping multi-line editor that grows in place as text wraps, up to 20% of the terminal height (see §3.5).

**Conversation/tools split**

- The split is stored as `[tui].left-panel-width-percentage`; invalid values fall back to `70`.
- `Alt+Left` / `Alt+Right` resize the split by 1 percentage point when focus is in the conversation, changed-files, or tools panel.
- Resizing is clamped so each panel keeps at least 20 columns whenever the terminal is wide enough for both minimums plus the divider; otherwise the percentage is clamped to `1..99` and Terminal.Gui lays out the reduced panels.
- After a resize, the conversation is rewrapped at the new width. A successful resize is persisted to the config file immediately and the status bar briefly shows `split NN%` (restored after ~3 s); if persistence fails, the status bar shows the config error.
- Changing input height or terminal size resizes both panels and the divider together, because all three share the same bottom fill offset.

### 3.2 Keyboard Shortcuts

| Key | Action |
|-----|--------|
| `Ctrl+G` | Cancel the current agent turn (any panel focused). |
| `Ctrl+Q` | Quit the application |
| `Ctrl+C` / `Ctrl+Insert` | Copy selection in the input |
| `Tab` | In the input with a `/` prefix: open completion popup. Otherwise: cycle focus conversation → changed files → tools → input |
| `Esc` | Close the inspection overlay; refocus the approval frame when an approval is pending; otherwise focus the input bar (never exits the app) |
| `Alt+Left / Alt+Right` | Resize the conversation/tools split and persist `[tui].left-panel-width-percentage` |
| `↑ / ↓`, `PgUp / PgDn`, `Home / End` | Scroll the focused panel |
| `Ctrl+Left / Ctrl+Right` | Horizontal scroll by 10 columns (conversation, tools, inspection) |
| `Ctrl+Home / Ctrl+End` | Jump to first/last tool row |

Typing a printable character while a content panel has focus redirects focus to the input bar and inserts the character there.

### 3.3 Slash Commands

Slash commands are entered in the input bar and handled synchronously by the TUI before the text is added as a user turn. Unknown commands are rejected inline with `unknown command: /<name> (try /help)`.

Typing `/` as the first character in the input opens the slash-command selection popup immediately, without requiring `Tab`. The user can keep typing to filter command names. `Esc` closes only the popup and returns focus to the input with the current text preserved, so normal typing can continue. When a command is highlighted, `Enter` inserts that command into the input field; it does not execute the command until the user submits the input field normally. If the inserted command accepts arguments, the replacement should include a trailing space so argument selection can begin naturally.

Pressing `Space` after a valid slash command opens that command's parameter-selection popup when the command has selectable parameters. Parameter selection uses the same popup interaction as command selection: typing filters available values, `Esc` dismisses and preserves the typed input, and `Enter` inserts the highlighted parameter value into the input field. Commands without selectable parameters do not open a parameter popup after `Space`.

| Command | Arguments | Effect |
|---------|-----------|--------|
| `/help` | none | Print the slash-command help text in the conversation panel. |
| `/clear` | none | Clear the visible conversation panel, tool log, and changed-files panel for the current TUI view. |
| `/tools` | none | List all tool definitions currently registered and sent to the LLM, including descriptions. |
| `/compact` | none | Run manual conversation compaction for the current session. If the agent is busy, asks the user to cancel the current turn first. |
| `/verbose` | none | Toggle inline verbose mode for tool calls and tool results; running it again turns verbose mode off. |
| `/config` | none | Show the active config file path and current configuration values grouped by section. |
| `/config list` | none | List every settable configuration key, type, and description. |
| `/config <key> <value>` | config key and value | Update a config value via `ConfigEditor.Set`. Model-setting changes reload the provider immediately when idle, or after the current turn completes when busy. |
| `/model` | none | Show current provider, model ID, and API-key source. |
| `/model <id>` | model ID | Switch the in-memory model ID for the active session and update the status bar. |
| `/resume` | none | Resume the most recent saved session for the current working directory. |
| `/resume <id>` | session ID | Resume a specific saved session ID. |
| `/resume list` | none | List the five most recent saved sessions for the current working directory. |
| `/sec` | none | Show a security summary of the tool permissions currently in effect. |
| `/self` | none | Ask the agent to summarize the current Dotsy runtime, configuration, environment, and command usage from a generated self-context prompt. |
| `/self <question>` | question text | Ask a question about the current Dotsy runtime or usage with the generated self-context prompt injected as context. |
| `/skill` | none | List discovered skills. |
| `/skill <name>` | skill name | Load the named skill body into the current loop context immediately, bypassing model-selected skill loading. |
| `/add <path>` | file path | Add a file path to read-only context for the current loop. Relative paths are resolved from the session cwd. |
| `/undo` | none | Reset tracked files to the previous git checkpoint for the current session turn, if one exists. Requires the current agent turn to be idle. |
| `/exit` | none | Quit the TUI. |
| `/quit` | none | Alias for `/exit`. |

**Slash-command popup:** a framed `ListView` shown directly above the input bar. It appears live when the input begins with `/` (or is forced open with `Tab`), sizes itself to the matches (width 20–60 columns, height 3–9 rows), and shows command names until a complete command plus trailing space switches it into parameter-selection mode. `↑ / ↓` navigates, `Enter` applies the selected command or parameter to the input field, `Tab` also applies the selected item when the popup is open, and `Esc` dismisses the popup without clearing the input.

**Selectable command parameters:**

| Command | Selectable parameter values |
|---------|-----------------------------|
| `/add <path>` | Files and directories relative to the current working directory; directories insert with a trailing path separator so selection can continue. |
| `/config <key> <value>` | First-level choices are `list` and config sections from `ConfigEditor.ParamList`. Selecting `list` inserts `/config list`. Selecting a section replaces the popup contents with keys in that section; selecting a key inserts `/config <section.key> ` and leaves the value typable. If the selected key is boolean or declares a finite set of valid values, pressing `Space` after `/config <section.key>` opens a value-selection popup with those values. Free-form values remain typable for unconstrained keys. |
| `/model <id>` | Model IDs loaded from the currently selected provider. The first lookup for a provider may be asynchronous; results are cached per provider for the duration of the TUI session and reused until the session exits. The current configured model is included even if provider listing is unavailable. |
| `/resume <id>` | First-level choices are `list` and `select session`. Selecting `list` inserts `/resume list`. Selecting `select session` replaces the popup contents with days derived from the current working directory's saved sessions list. Selecting a day replaces the popup contents with that day's session IDs in descending updated-time order; selecting a session ID inserts `/resume <id>`. |
| `/skill <name>` | Discovered skill names from the active skill search path. |

#### 3.3.1 `/self` Context Prompt

`/self` is a diagnostic and usage-query command. It does not print raw diagnostics directly. Instead, it builds an internal user prompt containing a structured Markdown self-context section, appends that prompt to the current conversation, and runs the normal agent loop so the model can answer from current Dotsy state.

If the command is entered without arguments, the generated user prompt asks for a concise summary of the current Dotsy runtime and notable configuration. If arguments follow `/self`, that text is used as the user question after the Markdown self-context section, for example `/self how do I switch models?`.

The Markdown self-context includes:

| Section | Fields |
|---------|--------|
| `app` | Dotsy product name, executable path when available, assembly informational version, build version, process ID, session ID, active provider, active model ID, TUI/headless mode. |
| `folder` | Current working directory, resolved git root when present, git branch, changed-file counts, untracked-file count, detected solution/project files, presence of project `.dotsy/config.toml`, presence of AGENTS/agent-instruction files. |
| `configuration` | Every key from `ConfigEditor.ParamList`, grouped by section, showing current value, type, description, and whether the value came from default/global/project/env/CLI when source tracking is available. |
| `secrets` | Secret-bearing config values such as provider API keys are never included verbatim. They are represented as `set:redacted`, `placeholder`, or `not set`. If source is known, include it as metadata, for example `set:redacted (env: ANTHROPIC_API_KEY)`. |
| `system` | OS/platform, architecture, .NET runtime version, current shell, host name when available, logical CPU count, process start time, elapsed time since startup, process memory used, available system memory, process thread count, and sampled process CPU usage. |
| `gpu` | Whether a GPU is detected, GPU model name(s), driver/runtime details when available, total GPU memory, available GPU memory, and current GPU memory used by the process when available. If detection is unsupported, report `unknown` rather than guessing. |
| `commands` | Complete slash-command catalog with syntax and descriptions, generated from the same catalog used by `/help` and completion. This includes aliases such as `/quit`. |
| `tools` | Count of registered LLM tools and optionally the same compact name/description list exposed by `/tools`, so `/self` can answer questions about tool availability. |

Secret redaction must preserve state without leaking values:

| Raw state | Self-context value |
|-----------|--------------------|
| Non-empty secret that is not a known placeholder | `set:redacted` |
| Empty, null, or whitespace secret | `not set` |
| Placeholder-looking value such as `TODO`, `changeme`, `your-api-key`, `<api-key>`, or `sk-...` examples from docs | `placeholder` |

The command should avoid expensive or fragile probing in the UI thread. Filesystem, git, GPU, and system-memory probes run off the UI thread with short timeouts. Missing optional probes should degrade to `unknown` and include a short reason only when useful to the user. The generated prompt should be capped to the same tool-output-style limits used elsewhere: include summaries by default, not full config files, diffs, environment variables, or command outputs.

Example generated prompt shape:

```markdown
# Dotsy Self Context

Generated: 2026-06-07T18:42:10-07:00

## App

| Field | Value |
|-------|-------|
| Name | dotsy |
| Version | 1.0.0 |
| Mode | tui |
| Session ID | abc123 |
| Provider | anthropic |
| Model | claude-sonnet-4-6 |

## Folder

| Field | Value |
|-------|-------|
| CWD | C:\dev\ai\dotsy |
| Git root | C:\dev\ai\dotsy |
| Branch | main |
| Modified files | 2 |
| Untracked files | 1 |

## Configuration

| Key | Type | Value | Source | Description |
|-----|------|-------|--------|-------------|
| model.provider | string | anthropic | default | active provider: anthropic, openai, ollama, azure_openai, compatible, gemini |
| model.anthropic.api_key | string | set:redacted | env:ANTHROPIC_API_KEY | Anthropic API key |
| model.openai.api_key | string | not set | default | OpenAI API key |

## System

| Field | Value |
|-------|-------|
| Platform | Windows |
| Architecture | x64 |
| .NET | 10.0.0 |
| Logical CPUs | 16 |
| Available memory MB | 18432 |
| Process memory MB | 412 |
| Process threads | 31 |
| Process CPU percent | 2.4 |
| Uptime | 00:18:23 |

## GPU

| Field | Value |
|-------|-------|
| Present | true |
| Model | NVIDIA GeForce RTX 4080 |
| Total memory MB | 16376 |
| Available memory MB | 12044 |
| Process memory MB | 512 |

## Commands

| Syntax | Description |
|--------|-------------|
| /help | Print the slash-command help text in the conversation panel. |
| /self [question] | Ask about the current Dotsy runtime, configuration, environment, and command usage with generated self-context. |

## User Question

How do I switch models?
```

#### 3.3.2 `/sec` Security Summary

`/sec` prints a concise security summary for the current session. It is intended to answer "what can the agent do right now?" without requiring the user to inspect config files, transient approvals, or hidden permission state.

The command is display-only: it appends the rendered summary to the conversation panel and does not mutate permission state, write permission files, request approval, or start an agent turn. The permission architecture and `/sec` output contract are defined in section 16.

### 3.4 Rendering Architecture

Terminal.Gui v2 runs a single-threaded message pump (`Application.Run()`). All view mutations happen on the UI thread; the agent loop runs on a background `Task` that drains the `IAsyncEnumerable<LoopEvent>` stream and posts each UI mutation via `Application.Invoke(...)`.

**Conversation model.** The conversation is held as a list of logical lines of Terminal.Gui `Cell`s, so every character carries its own colour attribute. On each append or width change the lines are word-wrapped at the current panel width (attributes are preserved exactly across wrap boundaries — no colour bleeding) and loaded into a read-only `ScrollableText`. Inline diff lines are flagged no-wrap so their full-width background colouring stays intact.

**Markdown rendering** is done by a `MarkdownRenderer` that consumes streamed text and emits coloured spans: fenced code blocks (with keyword/type/string/number colouring via `SyntaxHighlighter`), inline code, `**bold**`, `*italic*`, and `# headings`. Full CommonMark is not needed — just the subset the LLM commonly produces.

**Tool log** rows are immutable `ToolRow` records in an `ObservableCollection`, replaced in place by index as a tool's status, argument, or output changes.

### 3.5 Input Controls

The input bar is a single `MultilineInput` (a `TextView` subclass with word-wrap) pinned to the bottom of the screen. There is no separate full-screen multi-line editor; the field itself grows.

- **Auto-grow:** as text wraps or contains newlines, the field expands upward, up to 20% of the terminal height; beyond that the content scrolls. Deleting text shrinks the field again.
- `Enter` submits the trimmed text as one user turn (empty input is ignored).
- `↑ / ↓` cycle message history when the cursor is on the first/last line; on interior lines they move the cursor. The in-progress draft is restored when cycling past the newest entry.
- Standard editing: cursor movement, `Shift`+arrows / `Shift+Home/End` selection, `Ctrl+C` / `Ctrl+Insert` copy when a selection exists, cut/paste via the terminal.
- **Paste handling:** printable characters are coalesced in a short (5 ms) buffer so a paste flood arrives as a single insert. A newline arriving mid-paste is inserted literally rather than submitting, so multi-line pastes land in the field intact and can be reviewed before `Enter` sends them.

While the agent is running, submitting is rejected with `[Agent is busy — press Ctrl+G to cancel]`.

### 3.6 Confirmation and Decision Prompts

**Tool approval (overlay)**

When the agent loop yields a `PermissionRequired` event, a `FrameView` (` Tool approval `, height 6) appears above the input bar. The buttons are laid out in one row when they fit:

```
┌─ Tool approval ─────────────────────────────────────────────────────┐
│   Shell  rm -rf build/                                              │
│                                                                     │
│  [Allow once]   [Always allow]   [Deny]   [Allow for project]       │
│                                                                     │
└─────────────────────────────────────────────────────────────────────┘
```

- Focus jumps to the frame; `Tab` cycles the buttons; `Enter` activates the focused one.
- `Esc` refocuses the approval frame — it does not deny and does not exit the app.
- `[Allow for project]` is shown only for write-capable tools (`Write`, `Edit`, `MultiEdit`); it approves future writes inside the project folder (writes into `.dotsy/` still prompt — see section 16).
- `[Always allow]` persists a `{ToolName}({argument})` rule to `.dotsy/permissions.json`; `[Allow once]` allows for the session.
- The agent loop suspends on a `TaskCompletionSource<PermissionDecision>` until a button fires; on any choice the frame hides and focus returns to the input bar.

**Busy-state guards**

Commands that conflict with a running turn (`/compact`, `/undo`, `/resume`, `/self`, new prompts) are rejected with an inline warning asking the user to cancel the current turn first.

### 3.7 Status Bar and Progress States

Single `Label` row at `Y=0`, rebuilt as a formatted string on the UI thread:

```
  dotsy  ·  {session-id}  ·  {model}  ·  [{ctx%} ctx]  {spinner} {state}
```

| State | Display |
|-------|---------|
| Idle | `idle` (also `idle  [cancelled]`, `idle  [error]`, `idle  [turn limit]`, `idle  [context full]` after the corresponding loop end) |
| Waiting / streaming | `thinking…` with spinner cycling `◐ ◓ ◑ ◒` at 150 ms |
| Tool running | `running  {name}` |
| Compacting | `compacting...` |
| Retry pending | `⏳ retrying in {N}s · attempt {x}/{max}` |
| Split resize | `split NN%` (shown ~3 s, then restored) |

States beginning with an animated word (`thinking`, `running`, `compacting`) animate by cycling capitalization: letters turn uppercase left-to-right, then back to lowercase, advancing one letter per spinner tick.

The whole status bar changes colour with context usage: `≤50%` default (white on dark gray), `51–80%` black on yellow, `>80%` white on red.

**Streaming cursor:** while tokens are arriving, a `▌` cell is appended after the latest text and blinked on/off every 500 ms by a timer. It is removed when the stream ends, a tool starts, or the turn completes.

**Tool log timer:** each `RUNNING` row shows elapsed seconds; on completion the status changes to `OK`/`ERR` (or `SKIP` for deduplicated calls) and the duration freezes.

**Retry countdown:** the status bar counts down each second; the input stays usable, and `Ctrl+G` cancels the pending retry.

**Compaction notice:** inserted into the conversation as a dim divider:

```
─── compacted (48,213→14,002 tokens) ───
```

**Errors:** non-retryable errors appear inline in the conversation in red (indented error lines), and the status bar switches to `idle  [error]`. The input bar remains active so the user can recover (e.g. `/model`, `/config`) without restarting.

**Verbose mode** (`/verbose`): tool calls render inline as `▶ {name} · {arg}` and results as an indented dim preview capped at 20 lines.

### 3.8 Changed Files and Diff Inspection

After every turn that wrote files, the changed-file list is refreshed from `LibGit2Sharp` (staged + unstaged changes relative to HEAD) and shown in the changed-files panel at the bottom of the conversation frame.

Row format:

| Change | Rendering |
|--------|-----------|
| Added | `  + path` |
| Deleted | `  - path` |
| Modified | `  ↳ path   +N  -N` (counts from `ContentChanges.LinesAdded / LinesDeleted`) |

`↑ / ↓` move the selection; `Enter` opens the file in the **inspection overlay** with a header (`path  [modified|new file|deleted]`, `+N added  -N deleted`) followed by the coloured diff.

Diff colour coding (used both inline in the conversation and in the inspection overlay):

| Line | Colour |
|------|--------|
| Added | bright green on green background, line number gutter |
| Removed | bright red on red background, line number gutter |
| Hunk header | cyan |
| Context | default, dim line numbers |

Added/removed lines are padded to the panel width so the background colour forms a full-width bar, and are excluded from word-wrap.

### 3.9 Inspection Overlay

A full-width `InspectionFrameView` drawn over both panels (below the status bar), used for tool-output inspection and file diffs. While visible, the input bar and prompt are hidden; `Esc` closes it and returns focus to the tool list.

Opening a tool row (`Enter` in the tool log) shows:

```
  Command  {name} {arg}
  Folder   {cwd}
  Status   OK  (1s 2026-06-07T18:42:10.0000000-07:00)
  Output
  {tool output…}
```

- For `Write`, the content being written is shown as the output preview.
- For `Edit` / `MultiEdit`, the view shows the output followed by the parsed input parameters: `Path`, then per-edit `Lines N – M` or a `Search:` block (yellow) and a `Replace:` block (green).
- Tools with no recorded output show `(no output recorded)`.

### 3.10 Scrollbars

Scrollable surfaces draw Borland Pascal-style scrollbars only when content exceeds the viewport. The bar is part of the owning view's draw pass (not a separate `ScrollBarView` child), so it appears, disappears, and resizes automatically after content changes, split resizing, input-height changes, or terminal resizing. The bar geometry is produced by `ScrollBar.Build`, a pure static method that returns the bar as a glyph string (unit-tested in `ScrollBarTests`); `ScrollBar.DrawVertical`/`DrawHorizontal` stamp that string down a column or across a row.

| Surface | Bars |
|---------|------|
| Conversation panel (`ScrollableText`) | vertical; horizontal for long unwrapped lines (e.g. diff rows) |
| Tool log (`ToolListView`) | vertical (horizontal movement is keyboard-only, no bar) |
| Inspection overlay (`ScrollableText` content view) | vertical and horizontal |

Anatomy (vertical):

```
▲   ← up arrow (always present)
█   ← thumb (position proportional to scroll offset)
░   ← empty track
▼   ← down arrow (always present)
```

Horizontal bars use `◄` and `►` ends with the same `█`/`░` track. Thumb size = `max(1, trackLength * visible / total)`; thumb position is proportional to the scroll offset over the scrollable range. Degradation: a 1-cell bar renders a single `░`; a 2-cell bar renders only the two arrows. When both bars are visible, the horizontal bar is shortened by one column so it doesn't collide with the vertical bar's bottom cap in the corner.

### 3.11 Terminal.Gui v2 Control Map

| UI element | Control | Key properties |
|------------|---------|----------------|
| Status bar | `StatusBar : Label` (`Height=1, Y=0`) | Text rebuilt on UI thread; colour scheme switches with ctx usage |
| Conversation frame | `FrameView` | `Title=" Conversation "`, `Width=Dim.Percent(split)`, top/bottom border only |
| Conversation content | `ScrollableText : TextView` | read-only, cell-based content, overflow-only vertical + horizontal scrollbars |
| Changed files frame | `FrameView` | docked to conversation bottom, hidden when no changes |
| Changed files list | `FileListView : ListView` | `FileRow` items, `Enter` opens diff |
| Divider | `PaneDivider : View` | 1 column, draws `┬ │ ┴` |
| Tool log frame | `FrameView` | `Title=" Tools "`, fills remaining width |
| Tool log list | `ToolListView : ListView` | `ToolRow` items; keyboard horizontal scrolling; overflow-only vertical bar |
| Inspection overlay | `InspectionFrameView : FrameView` + `ScrollableText` | full-width; the `ScrollableText` content view draws both scrollbars |
| Approval overlay | `FrameView` + 4 × `FlatButton` | `Height=6`, hidden by default |
| Input bar | `MultilineInput : TextView` | `Y=Pos.AnchorEnd(h)`, grows to 20% of screen height |
| Completion popup | `FrameView` + `ListView` | above input; sized to matches (20–60 × 3–9) |
| Spinner | `ProgressSpinner` (`System.Threading.Timer`) | frames `◐ ◓ ◑ ◒` at 150 ms |

### 3.12 Colour Scheme

A VS Code Dark+-inspired 16-colour palette, defined centrally in a `Palette` class:

| Element | Attribute |
|---------|-----------|
| Status bar | White on DarkGray (yellow/red background at >50% / >80% ctx) |
| User prefix / commands | BrightCyan |
| Assistant text | Gray (normal) / White (bright) |
| Dim text (think, dividers, hints) | DarkGray |
| Success / OK | BrightGreen |
| Errors / ERR | BrightRed |
| Warnings / RUNNING | BrightYellow |
| Diff add | BrightGreen on Green |
| Diff delete | BrightRed on Red |
| Code / syntax | green text; keyword cyan, type green, string yellow, number magenta |
| Focused panel border | White (vs Gray unfocused) |

#### 3.12.1 Themes

The `[tui].theme` config key selects the palette: `dark | light | system | borland`. All colours flow through the `Palette` class, so a theme is a complete replacement set of attributes; no view hardcodes colours. Unknown theme names fall back to `dark` with a startup warning (to stderr).

A theme is modelled as a `Theme` object holding one attribute per semantic role (`Normal`, `Cmd`, `DiffAdd`, `SynKeyword`, `StatusBg`, …). `Palette` exposes those roles as properties that read from the active `Theme`, plus `ColorScheme` builders (`Scheme()`, `BtnScheme()`, `StatusScheme()`, …) composed from them. `Palette.Apply(name)` resolves the name (validating it, resolving `system`, falling back to `dark`) and swaps the active theme. `system` detection is best-effort via the `COLORFGBG` environment variable (a light background index of 7 or 15 ⇒ `light`); when no signal is available (e.g. Windows Terminal) it falls back to `dark`. `/self` reports both the configured value and the resolved concrete theme.

| Theme | Intent |
|-------|--------|
| `dark` | The VS Code Dark+-inspired palette above (current default). |
| `light` | Dark text on a light background for light terminals; same semantic roles re-mapped. |
| `system` | Detect the terminal/system light-dark preference and resolve to `dark` or `light` at startup. |
| `borland` | Borland Turbo Pascal 7 IDE homage: blue editor background, gray/white text, classic gray dialog chrome. |

**`borland` theme palette**

| Element | Attribute |
|---------|-----------|
| Conversation / panel background | Blue |
| Normal text | Gray on Blue |
| Bright text (assistant, headers) | White on Blue |
| Dim text (think, dividers, hints) | DarkGray on Blue |
| User prefix / commands | BrightYellow on Blue |
| Success / OK | BrightGreen on Blue |
| Errors / ERR | White on Red |
| Warnings / RUNNING | BrightYellow on Blue |
| Command entry field | BrightYellow on Blue (the `Input` role; TextView draws text via `ColorScheme.Focus`) |
| Status bar | Black on Gray (yellow/red background ctx warnings unchanged) |
| Approval overlay / dialogs | Black on Gray with BrightRed hotkeys |
| Selection / focused row | Black on Gray |
| Diff add | Black on Green |
| Diff delete | White on Red |
| Code / syntax | keyword White, type BrightCyan, string BrightYellow, number BrightMagenta — all on Blue |
| Scrollbars | Gray track on Blue (the `▲ █ ░ ▼` glyphs already match the Borland look) |

Exact attribute values for every theme are expected to need visual tuning on real terminals; the tables define intent, not pixel-perfect contracts.

#### 3.12.2 Live re-theme and scrollback recolouring

`/config tui.theme <name>` re-themes a running session without a restart. Re-theming has three parts:

1. **Live colour reads.** Anything drawn through a `Palette.*` property (new conversation/tool text as it is appended) picks up the new theme automatically on the next draw.
2. **Captured schemes.** Each view captures its `ColorScheme` at construction, so re-theme walks the view tree and reassigns schemes (`Palette.Scheme()` / `BtnScheme()`; `StatusBar` re-evaluates its own ctx-coloured scheme), then repaints.
3. **Already-rendered cells.** The conversation panel, tool-output, and file-diff stores hold baked `Cell` values — `Terminal.Gui`'s `Cell` is a `record struct` with no field for a semantic role, so the role can't be stored in the cell. Instead the role is *recovered* at switch time: because every theme is a finite, known set of attributes, `Palette.BuildRecolorMap(previousTheme)` reverse-maps each cell's current `Attribute` to its role in the outgoing theme, then re-resolves that role through the incoming theme. Cells whose attribute isn't part of the outgoing theme pass through unchanged.

This recolours existing scrollback in place, so a live theme switch updates the whole UI, not just new output. Two caveats follow from the reverse-map: roles that share an identical attribute *within* a theme are indistinguishable (harmless as long as such roles also share an attribute in the target theme — true for the bundled themes), and cells carrying an attribute foreign to the outgoing theme are left as-is. Switching always maps from the currently-active theme to the new one, so repeated switches compose correctly.
