# dotsy TUI theme / colour reference (`themes.html`)

`themes.html` is a self-contained, offline page that documents every colour dotsy's TUI paints,
for all four themes (**Dark / Light / Borland / Custom**). For each panel it shows a **palette
table** (role → colour, with a live-editable Custom theme) and a **rendered sample** using the
panel's real rendering logic. It is generated from a saved dotsy session plus the colour/rendering
rules copied out of the dotsy source.

This README is the recipe: with it (and Node + PowerShell) you can regenerate `themes.html` from
scratch, point it at a different session, or refresh it after the theme colours change in code.

---

## Files in this folder

| File | Role |
|------|------|
| `themes.html` | The generated output (open in a browser). |
| `generate.ps1` | **The generator.** Reads `convo-data.json`, emits `themes.html`. Contains the ported renderer (markdown + syntax highlighter + panel painters) and the full palette. Single source of truth. |
| `convo-data.json` | The sample conversation transcript (a trimmed session slice) baked into the page. |
| `extract.ps1` | Session `.jsonl` → `convo-data.json` (whole session, convo-relevant messages only). |
| `slice.js` | Node driver: trims a session to the smallest slice that still shows the most colours. |
| `make-slice.ps1` | Runs `slice.js` against a session using the renderer inside `generate.ps1`. |
| `driver.js` | Node batch analyzer: counts distinct colour roles per session. |
| `rank-sessions.ps1` | Runs `driver.js` over every `.dotsy/sessions` dir to rank sessions by colour variety. |

---

## Regenerate the page (the common case)

Everything needed is committed, so this works offline:

```powershell
cd themes
./generate.ps1        # reads convo-data.json -> writes themes.html
start themes.html
```

Use this after editing colours/roles/layout in `generate.ps1`, or after refreshing `convo-data.json`.

---

## How it was built (and how to redo each step)

### 1. Where the colours come from
Colours are **not** guessed — they are the exact bytes dotsy sends to the terminal. dotsy uses
Terminal.Gui, which renders its 16 named colours as **true-colour RGB** from a fixed table, so the
terminal's own scheme does not matter. The mapping `ColorName16 → RGB` is taken verbatim from:

- `extern/terminal.gui/Terminal.Gui/Drawing/Color/Color.ColorExtensions.cs` (the `ColorToName16Map`)
  e.g. `Gray = #808080`, `DarkGray = #767676`, `BrightGreen = #16c60c`, `White = #ffffff`.

Each theme's role → `ColorName16` assignment comes from:

- `src/Dotsy.Cli/Tui/Colors/Themes.cs` — the Dark / Light / Borland theme definitions.
- `src/Dotsy.Cli/Tui/Colors/Theme.cs`, `Palette.cs` — role names.

The `THEMES` object near the top of `generate.ps1` is the resolved result (role → hex per theme).
**If theme colours change in `Themes.cs`, update `THEMES` in `generate.ps1` to match**, then rerun.
Local overrides already applied there: Dark/Borland `Normal` = `#cccccc` (brighter body text),
Light `Warn`/`Running` = `#a16207` (readable amber on white).

### 2. Which panels / roles are reproduced
The rendering logic is ported to JS inside `generate.ps1`, mirroring these sources:

- **Conversation panel** — `AgentWindow.ResumeReplay.cs` (replay), `Renderers/MarkdownRenderer.cs`,
  `Renderers/SyntaxHighlighter.cs`. Roles: Cmd, Bullet, Normal, Dim, Bright, Code, Success, Err,
  Warn, Running, SynKeyword/Type/String/Number. (`Err`/`Warn`/`Running` can't occur in a replay, so
  the sample appends a "live-only" block with authentic strings for those three.)
- **Tools history panel** — `Tui/ToolList/ToolListView.cs`, `ToolRow.cs`, `AgentWindow.Tools.cs`
  (`OnToolRowRender`). Whole row coloured by status: OK=Success, ERR=Err, RUNNING=Running,
  SKIP/PENDING=Dim; group-bracket gutter=Dim; selected row=SelRow (highlight bar).
- **Changed files panel** — `Tui/FileList/FileListView.cs`. `+`=Success, `-`=Err, path=Bright,
  modified arrow/stats=Normal+Success+Err, selected row=SelRow.
- **Tool call detail (inspection)** — `AgentWindow.Tools.cs` (`ShowInspect`, `FormatEditInspectCells`)
  and `AgentWindow.cs` (`RenderUnifiedDiff`) + `Renderers/DiffRenderer.cs`. Headers=Bright,
  values/output=Normal, labels/folder/file-header=Dim, OK/additions/Replace=Success,
  ERR/deletions=Err, other-status/Search=Warn, diff hunk=DiffHdr, diff context=DiffCtx.
- **Approval panel** — `Tui/Approval/ApprovalView.cs`, `FlatButton.cs`, `Palette.cs`
  (`FocusedPanelScheme`/`BtnScheme`). Frame + title + buttons=Bright, prompt message=Normal,
  focused button=BtnFocus (highlight bar). The "Allow for project" button shows the cwd-relative
  project path for an out-of-cwd write (e.g. `Allow for ..\shared-lib`), via
  `PermissionStore.ApprovalProjectPath` — the two variants are both shown in the sample.

The page opens with a **table of contents** linking to each section (anchors on the `<h2>`s).

If dotsy's rendering changes, update the matching JS builder in `generate.ps1`
(`buildSegments`/`buildExtraSegments`, `toolsHistorySegs`, `changedFilesSegs`, `toolDetailSegs`).

### 3. The sample conversation data
`convo-data.json` holds `{ sessionId, count, messages:[{role:'user'|'agent', blocks:[...] }] }`.
It was produced by picking the session with the most colour variety, then trimming it:

```powershell
# (optional) find the richest session across all projects
./rank-sessions.ps1 -Root C:\dev

# rebuild convo-data.json from a chosen session, trimmed to ~100-150 lines that still
# hit every conversation colour (opening user turn + a code-heavy tail slice)
./make-slice.ps1 -Session C:\dev\ai\dotsy-development\.dotsy\sessions\20260617.15.jsonl

# or capture a whole session unmodified instead of slicing:
./extract.ps1 -Path <session.jsonl>
```

Then `./generate.ps1` to bake it in. Session logs live in each project's `.dotsy/sessions/*.jsonl`
(default `LogDir` = `.dotsy/sessions`, see `Dotsy.Core/Config/DotsyConfig.cs`).

---

## The Custom theme (interactive)
Every palette table has editable **Custom fg / Custom bg** hex boxes (seeded from Dark). Editing any
box validates the hex, updates the Custom swatch, syncs duplicate roles, and repaints the Custom
sample column in **every** panel live. Nothing is persisted — it's a scratch pad for trying colours.
To promote a value into a real theme, copy it into `Themes.cs`.

## Notes / limitations
- Node.js and PowerShell are required only for *regeneration*; the page itself is plain static HTML.
- Selected-row highlight bars are padded to a fixed width so the `SelRow` background reads as a bar;
  the real TUI fills to the panel edge.
- In a `--force16` / legacy-16-colour terminal, true-colour values snap to the nearest ANSI colour.
