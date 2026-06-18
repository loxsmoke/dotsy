using Dotsy.Core.Config;
using Dotsy.Core.Loop;
using Dotsy.Core.Providers;
using Dotsy.Core.Session;
using Terminal.Gui;
using TGAttribute = Terminal.Gui.Attribute;

namespace Dotsy.Cli.Tui;

public partial class AgentWindow
{
    #region Slash command handling
    private void HandleSlashCommand(string raw)
    {
        var parts = raw.TrimStart('/').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var cmd = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
        var rest = parts.Length > 1 ? string.Join(" ", parts[1..]) : "";

        switch (cmd)
        {
            case "exit":
            case "quit":
                Application.RequestStop();
                break;

            case "clear":
                ClearSessionCommand();
                break;

            case "help":
                ShowHelpCommand();
                break;

            case "tools":
                ToolsCommand();
                break;

            case "verbose":
                VerboseCommand();
                break;

            case "config":
                ConfigCommand(rest);
                break;

            case "model":
                ModelCommand(rest);
                break;

            case "resume":
                ResumeSession(rest);
                break;

            case "self":
                StartSelfCommand(raw, rest);
                break;

            case "sec":
                ShowSecuritySummary();
                break;

            case "skill":
                SkillCommand(rest);
                break;

            case "add":
                AddPathCommand(rest);
                break;

            case "compact":
                StartManualCompaction();
                break;

            case "undo":
                UndoCommand();
                break;

            default:
                AppendConvo($"unknown command: /{cmd}  (try /help)\n\n", Palette.Warn);
                break;
        }
    }

    private void ClearSessionCommand()
    {
        // Start a fresh session
        var config = TuiSessionContext.Config;
        var cwd = TuiSessionContext.Cwd;
        var sessionDir = SessionStore.ResolveDir(config.Session.LogDir, cwd);
        var newSessionId = SessionStore.NextId(sessionDir);
        var sessionStore = new SessionStore(newSessionId, sessionDir);
        var loopCtx = new LoopContext(newSessionId);
        TuiSessionContext.LoopCtx = loopCtx;
        TuiSessionContext.Session = sessionStore;

        // Clear conversation and reload
        conversationLines.Clear();
        conversationLines.Add([]);
        noWrapLineIndices.Clear();
        lock (streamCursorLock)
            streamCursorVisible = false;
        ReloadConvo();
        toolCallRows.Clear();
        toolCallCount = 0;
        toolCallGroupSeq = 0;
        fileRows.Clear();
        fileFrame.Visible = false;
        convo.Height = Dim.Fill();
        leftFrame.SetNeedsDraw();
        // Show confirmation message
        AppendConvo("Started a fresh session\n\n", Palette.Success);
        
        // Update status bar to reflect new context
         UpdateStatusBarFromCtx();
     }

    private void ShowHelpCommand()
    {
        AppendConvo("Slash commands:\n", Palette.Bright);
        const int helpNameCol = 24; // "  " + 22-char syntax column
        var helpDescWidth = Math.Max(20, convo.Frame.Width - helpNameCol - 1);
        var helpContIndent = new string(' ', helpNameCol);
        foreach (var command in SlashCommandCatalog.Commands)
        {
            var lines = WordWrap(command.Description, helpDescWidth);
            AppendConvo($"  {command.Syntax,-22}", Palette.Cmd);
            AppendConvo((lines.Count > 0 ? lines[0] : "") + "\n", Palette.Normal);
            for (var i = 1; i < lines.Count; i++)
                AppendConvo(helpContIndent + lines[i] + "\n", Palette.Normal);
        }
        AppendConvo("\n", Palette.Normal);
    }

    private void ToolsCommand()
    {
        var registry = TuiSessionContext.Registry;
        if (registry is null)
        {
            AppendConvoError("tool registry not initialized");
            return;
        }
        var defs = registry.GetToolDefinitions();
        AppendConvo($"  {defs.Count} tools registered\n\n", Palette.Dim);
        const int nameCol = 22; // "  " + 20-char name
        var descWidth = Math.Max(20, convo.Frame.Width - nameCol - 1);
        var contIndent = new string(' ', nameCol);
        foreach (var t in defs.OrderBy(t => t.Name))
        {
            var lines = WordWrap(t.Description, descWidth);
            AppendConvo($"  {t.Name,-20}", Palette.Bright);
            AppendConvo(lines[0] + "\n", Palette.Normal);
            for (var i = 1; i < lines.Count; i++)
                AppendConvo(contIndent + lines[i] + "\n", Palette.Normal);
        }
        AppendConvo("\n", Palette.Normal);
    }

    private void VerboseCommand()
    {
        var cfg = TuiSessionContext.Config;
        cfg.Tui.Verbose = !cfg.Tui.Verbose;
        var state = cfg.Tui.Verbose ? "on" : "off";
        AppendConvo($"  verbose {state}  (tool calls and results will{(cfg.Tui.Verbose ? "" : " not")} appear inline)\n\n", Palette.Dim);
    }

    private void ConfigCommand(string rest)
    {
        var cfg = TuiSessionContext.Config;
        if (string.IsNullOrEmpty(rest))
        {
            // The active provider's model section (e.g. [model.openai]) is highlighted green.
            var activeProvider = cfg.Model.Provider.ToLowerInvariant() switch
            {
                "azure_openai" => "azure",
                var p => p,
            };
            var activeSection = $"model.{activeProvider}";

            AppendConvo($"  {ConfigEditor.ConfigFilePath}\n\n", Palette.Dim);
            foreach (var section in ConfigEditor.GetSections(cfg))
            {
                var headerColor = section.Header.Equals(activeSection, StringComparison.OrdinalIgnoreCase)
                    ? Palette.ActiveSection
                    : Palette.Bright;
                AppendConvo($"[{section.Header}]\n", headerColor);
                foreach (var kv in section.Kvs)
                {
                    AppendConvo($"  {kv.Key,-22}", Palette.Dim);
                    AppendConvo($"= {kv.Value}\n", kv.Empty ? Palette.Warn : Palette.Normal);
                }
                AppendConvo("\n", Palette.Normal);
            }
            AppendConvo("  /config <key> <value> to change  ", Palette.Dim);
            AppendConvo("e.g. /config model.anthropic.id claude-opus-4-7\n\n", Palette.Normal);
        }
        else if (rest.Trim() == "list")
        {
            AppendConvo("Available config keys:\n\n", Palette.Bright);
            foreach (var group in ConfigEditor.ParamList)
            {
                AppendConvo($"[{group.Section}]\n", Palette.Bright);
                foreach (var p in group.Params)
                {
                    AppendConvo($"  {p.Key,-36}", Palette.Normal);
                    AppendConvo($"{p.Type,-8}", Palette.Dim);
                    AppendConvo($"{p.Description}\n", Palette.Dim);
                }
                AppendConvo("\n", Palette.Normal);
            }
        }
        else
        {
            var spaceIdx = rest.IndexOf(' ');
            if (spaceIdx < 0)
            {
                AppendConvo("  usage: /config <key> <value>  e.g. /config model.provider anthropic\n\n", Palette.Warn);
                return;
            }
            var key = rest[..spaceIdx].Trim();
            var value = rest[(spaceIdx + 1)..].Trim();

            // A real config key is always "section.key"; a dotless first word almost always
            // means the user typed a sentence after a stray "/config " left by autocomplete.
            if (!key.Contains('.'))
            {
                AppendConvo($"  '{rest}' isn't a config command.\n", Palette.Warn);
                AppendConvo("  To chat with the agent, send your message without the leading /config.\n", Palette.Dim);
                AppendConvo("  To change a setting, use /config <section.key> <value> (try /config list).\n\n", Palette.Dim);
                return;
            }

            var (ok, msg) = ConfigEditor.Set(cfg, key, value, TuiSessionContext.ProjectConfigPath);
            if (ok)
            {
                AppendConvo($"  {key} ", Palette.Dim);
                AppendConvo($"= {value}\n", Palette.Success);
                AppendConvo($"  saved → {msg}\n", Palette.Dim);

                // Rebuild provider + loop whenever any model setting changes
                if (key.StartsWith("model.", StringComparison.OrdinalIgnoreCase) &&
                    TuiSessionContext.LoopFactory is { } factory)
                {
                    if (scenarioCts is not null)
                    {
                        AppendConvo("  (provider will reload after the current turn completes)\n", Palette.Warn);
                    }
                    else
                    {
                        try
                        {
                            TuiSessionContext.Loop = factory();
                            AppendConvo("  provider reloaded\n", Palette.Dim);
                        }
                        catch (Exception ex)
                        {
                            AppendConvoError($"provider reload failed: {ex.Message}");
                        }
                    }
                }

                // Live re-theme: re-resolve the palette, recolor existing cells, repaint.
                if (key.Equals("tui.theme", StringComparison.OrdinalIgnoreCase))
                {
                    var previousTheme = Palette.ActiveTheme;
                    var (resolved, fellBack) = Palette.Apply(value);
                    var recolor = Palette.BuildRecolorMap(previousTheme);
                    Application.Invoke(() => ReapplyTheme(recolor));
                    if (fellBack)
                        AppendConvo($"  unknown theme '{value}', using dark\n", Palette.Warn);
                    else if (resolved != value.Trim().ToLowerInvariant())
                        AppendConvo($"  resolved to {resolved}\n", Palette.Dim);
                }

                // Keep status bar in sync with model ID
                Application.Invoke(() => statusBar.SetModel(TuiSessionContext.Config.Model.ActiveModelId));
                AppendConvo("\n", Palette.Normal);
            }
            else
                AppendConvoError($"config error: {msg}");
        }
    }

    private void ModelCommand(string rest)
    {
        // If model is specified then change the active model
        if (!string.IsNullOrEmpty(rest))
        {
            TuiSessionContext.Config.Model.ActiveModelId = rest;
            Application.Invoke(() => statusBar.SetModel(rest));
            AppendConvo($"model → {rest}\n\n", Palette.Success);
            return;
        }


        var cfg = TuiSessionContext.Config;
        var provider = ConfigLoader.GetProviderDisplayName(cfg.Model.Provider);
        var keySource = ConfigLoader.GetApiKeySource(cfg);
        var activeId = cfg.Model.ActiveModelId;
        AppendConvo("  Provider:  ", Palette.Dim); AppendConvo($"{provider}\n", Palette.Normal);
        AppendConvo("  Model:     ", Palette.Dim); AppendConvo($"{activeId}\n", Palette.Bright);

        // Fetch context-window info OFF the UI thread. The lookup awaits an HTTP call
        // whose continuation is posted back to the Terminal.Gui main loop, so blocking
        // on .Result here deadlocks the UI thread. Append the remaining lines (in order)
        // once the lookup resolves, marshalling back via Application.Invoke.
        var lookup = TuiSessionContext.ModelInfoLookup;
        _ = Task.Run(async () =>
        {
            ModelInfo? info = null;
            var failed = false;
            try
            {
                if (lookup != null)
                    info = await lookup(activeId).ConfigureAwait(false);
            }
            catch { failed = true; }

            Application.Invoke(() =>
            {
                var context = "unknown";
                if (failed)
                {
                    context = "error fetching";
                }
                else if (info != null)
                {
                    context = (info?.ContextWindow.ToString() ?? "unknown");
                }

                AppendConvo("  Context:   ", Palette.Dim); AppendConvo($"{context}\n", Palette.Err);
                AppendConvo("  Api key:   ", Palette.Dim);
                if (keySource == ConfigLoader.NoKeyRequired)
                    AppendConvo($"{ConfigLoader.NoKeyRequired}\n\n", Palette.Bright);
                else if (keySource == ConfigLoader.KeyNotSpecified)
                    AppendConvo($"{ConfigLoader.KeyNotSpecified}\n\n", Palette.Bright);
                else
                    AppendConvo($"specified via {keySource}\n\n", Palette.Bright);
            });
        });
    }

    private void ResumeSession(string requestedId)
    {
        if (scenarioCts is not null)
        {
            AppendConvo("cancel the current turn before resuming a session\n\n", Palette.Warn);
            return;
        }

        var config = TuiSessionContext.Config;
        var cwd = TuiSessionContext.Cwd;
        var sessionDir = SessionStore.ResolveDir(config.Session.LogDir, cwd);
        var loaded = string.IsNullOrWhiteSpace(requestedId)
            ? SessionLoader.LoadMostRecent(sessionDir, cwd)
            : SessionLoader.Load(requestedId.Trim(), sessionDir);

        if (loaded is null)
        {
            var target = string.IsNullOrWhiteSpace(requestedId) ? "most recent session" : requestedId.Trim();
            AppendConvo($"session not found: {target}\n\n", Palette.Warn);
            return;
        }

        var loopCtx = new LoopContext(loaded.SessionId);
        loopCtx.Messages.AddRange(loaded.Messages);
        loopCtx.CompactionSummary = loaded.CompactionSummary;

        TuiSessionContext.LoopCtx = loopCtx;
        TuiSessionContext.Session = new SessionStore(
            loaded.SessionId,
            sessionDir,
            disabled: !config.Session.LogEnabled);
        if (TuiSessionContext.LoopFactory is { } factory)
            TuiSessionContext.Loop = factory();

        toolCallRows.Clear();
        toolCallCount = 0;
        toolCallGroupSeq = 0;
        fileRows.Clear();
        fileFrame.Visible = false;
        convo.Height = Dim.Fill();
        statusBar.SetSession(loaded.SessionId);
        UpdateStatusBarFromCtx();

        AppendConvo($"resumed session: {loaded.SessionId}\n", Palette.Success);
        AppendConvo($"messages loaded: {loaded.Messages.Count}\n\n", Palette.Dim);
    }

    private void StartSelfCommand(string rawCommand, string question)
    {
        if (scenarioCts is not null)
        {
            AppendConvo("[Agent is busy — press Ctrl+C to cancel]\n\n", Palette.Warn);
            return;
        }

        var loopCtx = TuiSessionContext.LoopCtx;
        if (loopCtx is null)
        {
            AppendConvoError("session context not initialized");
            return;
        }

        statusBar.SetState("building self context");
        Task.Run(async () =>
        {
            try
            {
                var builder = new SelfContextBuilder();
                var prompt = await builder.BuildPromptAsync(
                    new SelfContextRequest(
                        TuiSessionContext.Config,
                        loopCtx,
                        TuiSessionContext.Cwd,
                        TuiSessionContext.Registry,
                        SlashCommandCatalog.Commands,
                        Mode: "tui",
                        ResolvedTheme: Palette.ActiveName),
                    question);

                Application.Invoke(() =>
                {
                    statusBar.SetState("idle");
                    SubmitUserPrompt(rawCommand, prompt);
                });
            }
            catch (Exception ex)
            {
                Application.Invoke(() =>
                {
                    statusBar.SetState("idle");
                    AppendConvoError($"self context failed: {ex.Message}");
                });
            }
        });
    }

    private void ShowSecuritySummary()
    {
        var permissions = TuiSessionContext.Permissions;
        if (permissions is null)
        {
            AppendConvoError("permission store not initialized");
            return;
        }

        var summary = new SecuritySummaryRenderer().Render(new SecuritySummaryRequest(
            TuiSessionContext.Config,
            permissions,
            TuiSessionContext.Cwd,
            TuiSessionContext.Registry,
            TuiSessionContext.LoopCtx,
            Headless: false));

        AppendConvo(summary + "\n\n", Palette.Normal);
    }

    private void SkillCommand(string rest)
    {
        var disc = new SkillDiscovery(TuiSessionContext.Config.Skills, TuiSessionContext.Cwd);

        // List skills if no skill name is provided
        if (string.IsNullOrWhiteSpace(rest))
        {
            var skills = disc.FindAll();
            if (skills.Count == 0)
                AppendConvo("no skills found\n\n", Palette.Dim);
            else
            {
                AppendConvo("skills:\n", Palette.Bright);
                foreach (var s in skills)
                    AppendConvo($"  {s.Name,-20}  {s.FilePath}\n", Palette.Normal);
                AppendConvo("\n", Palette.Normal);
            }
            return;
        }

        var record = disc.Find(rest);
        var ctx = TuiSessionContext.LoopCtx;
        if (record is null)
        {
            AppendConvo($"skill not found: {rest}\n\n", Palette.Warn);
        }
        else if (ctx is null)
        {
            AppendConvoError("session context not initialized");
        }
        else
        {
            var skill = SkillLoader.Load(record);
            ctx.LoadedSkills[skill.Frontmatter.Name] = skill.Body;
            AppendConvo($"loaded skill: {skill.Frontmatter.Name}\n", Palette.Success);
            if (skill.CompanionPaths.Count > 0)
            {
                AppendConvo("companion files:\n", Palette.Dim);
                foreach (var p in skill.CompanionPaths)
                    AppendConvo($"  {p}\n", Palette.Dim);
            }
            AppendConvo("\n", Palette.Normal);
        }
    }

    private void AddPathCommand(string rest)
    {
        if (string.IsNullOrEmpty(rest))
        {
            AppendConvo("usage: /add <path>\n\n", Palette.Warn);
            return;
        }
        var abs = Path.IsPathRooted(rest)
            ? rest
            : Path.GetFullPath(Path.Combine(TuiSessionContext.Cwd, rest));
        var ctx = TuiSessionContext.LoopCtx;
        if (ctx is not null && !ctx.AddedFiles.Contains(abs))
        {
            ctx.AddedFiles.Add(abs);
            AppendConvo($"added: {rest}\n\n", Palette.Success);
        }
        else
        {
            AppendConvo($"already in context: {rest}\n\n", Palette.Dim);
        }
    }

    private void StartManualCompaction()
    {
        if (scenarioCts is not null)
        {
            AppendConvo("cancel the current turn before compacting\n\n", Palette.Warn);
            return;
        }

        var loop = TuiSessionContext.Loop;
        var loopCtx = TuiSessionContext.LoopCtx;
        var cwd = TuiSessionContext.Cwd;
        if (loop is null || loopCtx is null)
        {
            AppendConvoError("session context not initialized");
            return;
        }

        scenarioCts = new CancellationTokenSource();
        var ct = scenarioCts.Token;

        Task.Run(async () =>
        {
            var compacted = false;
            try
            {
                StartSpinner("compacting...");
                await foreach (var ev in loop.CompactAsync(loopCtx, cwd, ct))
                {
                    if (ev is CompactionOccurred co)
                    {
                        compacted = true;
                        Application.Invoke(() => AppendConvo(
                            $"\n─── compacted ({co.TokensBefore:N0}→{co.TokensAfter:N0} tokens) ───\n\n",
                            Palette.Dim));
                    }
                }

                if (!compacted)
                    Application.Invoke(() => AppendConvo("nothing to compact\n\n", Palette.Dim));
                UpdateStatusBarFromCtx();
            }
            catch (OperationCanceledException)
            {
                Application.Invoke(() => AppendConvo("\n[compact cancelled]\n\n", Palette.Warn));
            }
            catch (Exception ex)
            {
                Application.Invoke(() => AppendConvoError($"compact failed: {ex.Message}"));
            }
            finally
            {
                StopSpinner("ready");
                scenarioCts?.Dispose();
                scenarioCts = null;
            }
        });
    }

    private void UndoCommand()
    {
        if (scenarioCts is not null)
        {
            AppendConvo("cancel the current turn before undoing\n\n", Palette.Warn);
            return;
        }

        var ctx = TuiSessionContext.LoopCtx;
        if (ctx is null)
        {
            AppendConvoError("session context not initialized");
            return;
        }

        var git = new GitIntegration(TuiSessionContext.Cwd);
        if (git.Undo(ctx.SessionId, ctx.TurnCount))
        {
            AppendConvo("undone to previous checkpoint\n\n", Palette.Success);
            RefreshChangedFiles();
        }
        else
        {
            AppendConvo("no checkpoint found for this session\n\n", Palette.Warn);
        }
    }

    #endregion

    #region Slash completions
    private bool TryShowCompletionPopup()
    {
        UpdateCompletionPopup(force: true);
        if (completionItems.Count == 0) return false;

        completionFrame.Visible = true;
        completionList.SelectedItem = 0;
        completionList.SetFocus();
        return true;
    }

    private void UpdateCompletionPopup(bool force = false)
    {
        if (!force && !completionFrame.Visible) return;

        var text = (promptInput.Text?.ToString() ?? "").TrimEnd('\r', '\n');
        var items = BuildCompletionItems(text);

        completionItems.Clear();
        foreach (var item in items)
            completionItems.Add(item);

        if (completionItems.Count == 0)
        {
            HideCompletionPopup();
            return;
        }

        var height = Math.Clamp(completionItems.Count + 2, 3, 9);
        var width = Math.Clamp(completionItems.Max(i => i.Display.Length) + 4, 20, 60);
        completionFrame.Height = height;
        completionFrame.Width = width;
        completionFrame.Y = Pos.AnchorEnd(inputHeight + height);
        completionFrame.Visible = true;

        if (completionList.SelectedItem < 0 || completionList.SelectedItem >= completionItems.Count)
            completionList.SelectedItem = 0;
        completionList.EnsureSelectedItemVisible();
        completionFrame.SetNeedsDraw();
    }

    private List<CompletionItem> BuildCompletionItems(string text)
    {
        if (!text.StartsWith('/') || text.Contains('\n'))
            return [];

        var body = text[1..];
        var firstSpace = body.IndexOf(' ');
        if (firstSpace < 0)
        {
            var prefix = body;
            return SlashCommandCatalog.Names
                .Where(c => c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(c => new CompletionItem("/" + c, "/" + c + " "))
                .ToList();
        }

        var cmd = body[..firstSpace].ToLowerInvariant();
        var rest = body[(firstSpace + 1)..];
        return cmd == "add" ? BuildAddPathCompletions(rest) : [];
    }

    private List<CompletionItem> BuildAddPathCompletions(string partial)
    {
        var cwd = TuiSessionContext.Cwd;
        var normalized = partial.TrimStart();
        var rooted = Path.IsPathRooted(normalized);
        var endsWithSeparator = normalized.EndsWith(Path.DirectorySeparatorChar)
            || normalized.EndsWith(Path.AltDirectorySeparatorChar);

        string searchDir;
        string prefix;
        string typedDir;

        if (endsWithSeparator)
        {
            typedDir = normalized;
            searchDir = rooted ? normalized : Path.GetFullPath(Path.Combine(cwd, normalized));
            prefix = "";
        }
        else
        {
            typedDir = Path.GetDirectoryName(normalized) ?? "";
            prefix = Path.GetFileName(normalized);
            searchDir = string.IsNullOrEmpty(typedDir)
                ? cwd
                : rooted
                    ? typedDir
                    : Path.GetFullPath(Path.Combine(cwd, typedDir));
        }

        if (!Directory.Exists(searchDir))
            return [];

        static bool StartsWithPrefix(string name, string prefix) =>
            string.IsNullOrEmpty(prefix)
            || name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

        string BuildReplacement(string name, bool isDir)
        {
            var completed = string.IsNullOrEmpty(typedDir)
                ? name
                : Path.Combine(typedDir, name);
            if (isDir && !completed.EndsWith(Path.DirectorySeparatorChar))
                completed += Path.DirectorySeparatorChar;
            return "/add " + completed;
        }

        var dirs = Directory.EnumerateDirectories(searchDir)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n) && StartsWithPrefix(n!, prefix))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(n => new CompletionItem(n! + Path.DirectorySeparatorChar, BuildReplacement(n!, isDir: true)));

        var files = Directory.EnumerateFiles(searchDir)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n) && StartsWithPrefix(n!, prefix))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(n => new CompletionItem(n!, BuildReplacement(n!, isDir: false)));

        return dirs.Concat(files).Take(12).ToList();
    }

    private void MoveCompletionSelection(int delta)
    {
        if (completionItems.Count == 0) return;
        var next = Math.Clamp(completionList.SelectedItem + delta, 0, completionItems.Count - 1);
        completionList.SelectedItem = next;
        completionList.EnsureSelectedItemVisible();
        completionList.SetNeedsDraw();
    }

    private void ApplySelectedCompletion()
    {
        if (!completionFrame.Visible || completionItems.Count == 0)
            return;

        var idx = Math.Clamp(completionList.SelectedItem, 0, completionItems.Count - 1);
        var item = completionItems[idx];
        HideCompletionPopup();
        promptInput.SetTextAndMoveEnd(item.Replacement);
        ResizeInput();
        promptInput.SetFocus();
    }

    private void HideCompletionPopup()
    {
        completionFrame.Visible = false;
        completionItems.Clear();
        completionFrame.SetNeedsDraw();
    }
    #endregion

    #region Theme re-application
    // Re-applies the active theme to the whole view tree without a restart. Color attributes used
    // when drawing (Palette.*) resolve live, but ColorSchemes are captured per view, so reassign
    // them here. When a recolor map is supplied, already-rendered cells (conversation scrollback,
    // tool output, file diffs) are remapped from the previous theme's attributes to the new ones.
    private void ReapplyTheme(Func<TGAttribute, TGAttribute>? recolor = null)
    {
        if (recolor is not null)
        {
            RecolorCellLines(conversationLines, recolor);
            foreach (var row in toolCallRows)
                if (row.Output is { } output) RecolorCellLines(output, recolor);
            foreach (var row in fileRows)
                RecolorCellLines(row.Diff, recolor);
        }

        ColorScheme = Palette.Scheme();
        Colors.ColorSchemes["Base"] = Palette.Scheme();
        Colors.ColorSchemes["Menu"] = Palette.Scheme();
        Colors.ColorSchemes["Dialog"] = Palette.Scheme();
        Colors.ColorSchemes["Error"] = Palette.Scheme();
        RethemeRecursive(this);
        ReloadConvo();
        SetNeedsDraw();
    }

    private static void RecolorCellLines(List<List<Cell>> lines, Func<TGAttribute, TGAttribute> recolor)
    {
        foreach (var line in lines)
            for (int j = 0; j < line.Count; j++)
            {
                var cell = line[j];
                if (cell.Attribute is { } a)
                    line[j] = cell with { Attribute = recolor(a) };
            }
    }

    private static void RethemeRecursive(View view)
    {
        foreach (var sv in view.Subviews)
        {
            switch (sv)
            {
                case StatusBar sb: sb.ApplyTheme(); break;
                case FlatButton btn: btn.ColorScheme = Palette.BtnScheme(); break;
                case MultilineInput mi: mi.ColorScheme = Palette.InputScheme(); break;
                case ScrollableText st: st.ColorScheme = Palette.ReadOnlyTextScheme(); break;
                default: sv.ColorScheme = Palette.Scheme(); break;
            }
            RethemeRecursive(sv);
        }
    }
    #endregion

}
