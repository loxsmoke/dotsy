using Dotsy.Cli.Tui.Approval;
using Dotsy.Cli.Tui.Colors;
using Dotsy.Cli.Tui.Renderers;
using Dotsy.Core.Loop.Data;
using Dotsy.Core.Providers;
using Dotsy.Core.Session.Data;
using Dotsy.Core.Tools;
using Dotsy.Core.Utils;

namespace Dotsy.Cli.Tui;

public partial class AgentWindow
{
    private void SubmitUserPrompt(string displayText, string promptText)
    {
        if (scenarioCts is not null)
        {
            AppendConvo("[Agent is busy — press Ctrl+G to cancel]\n\n", Palette.Warn);
            return;
        }

        var loop = TuiSessionContext.Loop;
        var loopCtx = TuiSessionContext.LoopCtx;
        var cwd = TuiSessionContext.Cwd;

        if (loop is null || loopCtx is null)
        {
            AppendConvo("[Agent not initialized]\n\n", Palette.Err);
            return;
        }

        AppendConvo($"User › {displayText}\n\n", Palette.Cmd);

        // Tool calls persist across prompts; tag this prompt's calls with a fresh group id so the
        // panel can bracket them together. The changed-files panel still resets each prompt.
        var toolGroup = ++toolCallGroupSeq;
        fileRows.Clear();
        fileFrame.Visible = false;
        convo.Height = Dim.Fill();
        leftFrame.SetNeedsDraw();

        loopCtx.Messages.Add(new UserMessage([new TextBlock(promptText)]));
        TuiSessionContext.Session?.Append(new SessionRecord
        {
            Type = SessionRecordType.User,
            Cwd = cwd,
            Message = new { content = promptText }
        });

        loop.PermissionPrompter = async (tool, rawArgs, token) =>
        {
            var displayArg = FormatRunApproval(tool, rawArgs, cwd);
            var choice = await ShowApproval(tool, displayArg);
            switch (choice)
            {
                case ApprovalChoice.AlwaysAllow:
                    TuiSessionContext.Permissions?.AlwaysAllow(tool.Name, rawArgs);
                    break;
                case ApprovalChoice.AllowForProject:
                    TuiSessionContext.Permissions?.AllowWriteForProject();
                    break;
                case ApprovalChoice.AllowOnce:
                    TuiSessionContext.Permissions?.AllowForSession(tool.Name, rawArgs);
                    break;
            }
            return choice switch
            {
                ApprovalChoice.AllowOnce => PermissionDecision.AllowOnce,
                ApprovalChoice.AllowForProject => PermissionDecision.AllowForProject,
                ApprovalChoice.AlwaysAllow => PermissionDecision.AlwaysAllow,
                _ => PermissionDecision.Deny
            };
        };

        scenarioCts = new CancellationTokenSource();
        var ct = scenarioCts.Token;
        var toolTimers = new Dictionary<int, long>();
        var toolArgs = new Dictionary<int, (string Name, string ArgsJson)>();
        var toolRowIndex = new Dictionary<int, int>(); // per-turn loop index -> absolute row index
        var currentTurn = loopCtx.TurnCount;

        Task.Run(async () =>
        {
            try
            {
                StartSpinner("thinking…");
                bool assistantWritten = false;
                bool thinkingWritten = false;
                MarkdownRenderer? mdRenderer = null; // TODO: make it lazy<T>
                // Tools in flight this prompt. ToolStarted/ToolFinished are always balanced
                // (the loop-guard path also emits a ToolFinished per call), so this returns to
                // zero when every tool of a turn has finished. While it is zero the model - not
                // a tool - is what we are waiting on, so the status reads "thinking…" rather than
                // leaving the last "running <tool>" text up through the (often slow) model turn.
                int runningTools = 0;

                await foreach (var ev in loop.RunAsync(loopCtx, cwd, ct))
                {
                    switch (ev)
                    {
                        case TextChunk tc:
                            VerboseOutput(tc);
                            if (!assistantWritten)
                            {
                                assistantWritten = true;
                                mdRenderer = new MarkdownRenderer(convoWrapWidth, (text, attr) =>
                                    TuiSessionContext.App.Invoke(() => AppendConvo(text, attr)));
                                TuiSessionContext.App.Invoke(() => AppendConvo("\nAgent› ", Palette.Bullet));
                                StartStreamCursor();
                            }
                            HideStreamCursor();
                            mdRenderer!.Write(tc.Text);
                            ShowStreamCursor();
                            break;

                        case ThinkingChunk thk:
                            VerboseOutput(thk);
                            if (!thinkingWritten)
                            {
                                thinkingWritten = true;
                                TuiSessionContext.App.Invoke(() => AppendConvo("\nThink› ", Palette.Dim));
                            }
                            TuiSessionContext.App.Invoke(() => AppendConvo(thk.Text, Palette.Dim));
                            break;

                        case ToolStarted ts:
                            {
                                StopStreamCursor();
                                VerboseOutput(ts);
                                if (assistantWritten)
                                    mdRenderer?.Flush();
                                toolTimers[ts.Index] = Environment.TickCount64;
                                toolArgs[ts.Index] = (ts.Name, ts.Arg);
                                var displayArg = FormatPanelArgument(ts.Name, ts.Arg, cwd);

                                // Try to parse the tool arguments to show all parameters in the inspection panel
                                var parameters = "";
                                try
                                {
                                    if (ts.Arg.Length > 2 && ts.Arg.StartsWith('{') && ts.Arg.EndsWith('}'))
                                    {
                                        using var doc = System.Text.Json.JsonDocument.Parse(ts.Arg);
                                        if (doc.RootElement.TryGetProperty("path", out var pathElement))
                                        {
                                            // For Read/Write tools, show the path
                                            parameters = $"path: {pathElement.GetString() ?? ""}";
                                        }
                                        else if (doc.RootElement.TryGetProperty("content", out var contentElement))
                                        {
                                            // For Write tool, show content preview
                                            var content = contentElement.GetString() ?? "";
                                            parameters = $"content: {content.Length} chars";
                                        }
                                        else
                                        {
                                            // Show all parameters for other tools
                                            var props = new List<string>();
                                            foreach (var prop in doc.RootElement.EnumerateObject())
                                            {
                                                props.Add($"{prop.Name}: {prop.Value.GetString() ?? ""}");
                                            }
                                            parameters = string.Join(", ", props);
                                        }
                                    }
                                }
                                catch
                                {
                                    // If parsing fails, just show the raw arguments
                                }

                                // Rows accumulate across prompts, so the loop's per-turn index is
                                // remapped to the absolute row index AddTool assigns.
                                var rowIdx = AddTool(ts.Name, displayArg, cwd, toolGroup, parameters.Length > 0 ? parameters : null, currentTurn);
                                toolRowIndex[ts.Index] = rowIdx;
                                // For Write, pre-populate inspect with the content being written
                                if (ts.Name == WriteTool.ToolName
                                    && ToolPanelFormatter.GetWriteContent(ts.Arg) is { } wc)
                                    SetToolOutput(rowIdx, FormatToolOutput(wc));
                                runningTools++;
                                TuiSessionContext.App.Invoke(() => statusBar.SetState($"running  {ts.Name}"));
                                if (TuiSessionContext.Config.Tui.Verbose)
                                    TuiSessionContext.App.Invoke(() =>
                                        AppendConvo($"\n▶ {ts.Name} · {displayArg}\n", Palette.Dim));
                                break;
                            }

                        case ToolFinished tf:
                            {
                                VerboseOutput(tf);
                                var elapsed = toolTimers.TryGetValue(tf.Index, out var start)
                                    ? (int)((Environment.TickCount64 - start) / 1000)
                                    : 0;
                                var status = tf.Result.IsError ? "ERR"
                                    : tf.Result.Content == "[skipped: duplicate]" ? "SKIP"
                                    : "OK";
                                var rowIdx = toolRowIndex.TryGetValue(tf.Index, out var ri) ? ri : tf.Index;
                                UpdateTool(rowIdx, status, elapsed);
                                List<List<Cell>>? editCells = null;
                                if (!tf.Result.IsError && toolArgs.TryGetValue(tf.Index, out var ta))
                                {
                                    if (ta.Name is EditTool.ToolName or MultiEditTool.ToolName)
                                        editCells = FormatEditInspectCells(ta.Name, ta.ArgsJson, tf.Result.Content, cwd);
                                    else if (FormatPanelResult(ta.Name, ta.ArgsJson, tf.Result.Content, cwd) is { } enriched)
                                        UpdateToolArg(rowIdx, enriched);
                                }
                                // Write success: keep the content preview set during ToolStarted
                                if (tf.Name != WriteTool.ToolName || tf.Result.IsError)
                                    SetToolOutput(rowIdx, editCells ?? FormatToolOutput(tf.Result.Content));
                                if (tf.Result.IsError)
                                    TuiSessionContext.App.Invoke(() => AppendConvo(
                                        $"  [✗ {tf.Name}] {tf.Result.Content}\n", Palette.Err));
                                else if (tf.Name.EqualsNoCase(DoneTool.ToolName) &&
                                         !string.IsNullOrWhiteSpace(tf.Result.Content))
                                {
                                    if (!assistantWritten)
                                        TuiSessionContext.App.Invoke(() => AppendConvo("\nAgent › ", Palette.Bullet));
                                    else
                                        mdRenderer?.Flush();

                                    TuiSessionContext.App.Invoke(() => AppendConvo(tf.Result.Content.TrimEnd() + "\n", Palette.Normal));
                                    assistantWritten = true;
                                }
                                else if (TuiSessionContext.Config.Tui.Verbose)
                                {
                                    var resultLines = tf.Result.Content
                                        .Split('\n')
                                        .Select(l => l.TrimEnd('\r'))
                                        .Where(l => l.Length > 0)
                                        .ToArray();
                                    const int maxLines = 20;
                                    var preview = string.Join("\n",
                                        resultLines.Take(maxLines).Select(l => "  " + l));
                                    var suffix = resultLines.Length > maxLines
                                        ? $"\n  … ({resultLines.Length - maxLines} more lines)"
                                        : "";
                                    TuiSessionContext.App.Invoke(() =>
                                        AppendConvo(preview + suffix + "\n", Palette.Dim));
                                }
                                // Once the last tool of this turn finishes we are back to waiting
                                // on the model; reflect that instead of leaving "running <tool>" up.
                                if (--runningTools <= 0)
                                {
                                    runningTools = 0;
                                    TuiSessionContext.App.Invoke(() => statusBar.SetState("thinking…"));
                                }
                                break;
                            }

                        case PermissionRequired pr:
                            {
                                StopStreamCursor();
                                VerboseOutput(pr);
                                var choice = await ShowApproval(pr.Tool, pr.DisplayArgument);
                                if (choice == ApprovalChoice.AllowForProject)
                                    TuiSessionContext.Permissions?.AllowWriteForProject();
                                pr.Decision.TrySetResult(choice switch
                                {
                                    ApprovalChoice.AllowOnce => PermissionDecision.AllowOnce,
                                    ApprovalChoice.AllowForProject => PermissionDecision.AllowForProject,
                                    ApprovalChoice.AlwaysAllow => PermissionDecision.AlwaysAllow,
                                    _ => PermissionDecision.Deny
                                });
                                break;
                            }

                        case CompactionOccurred co:
                            StopStreamCursor();
                            VerboseOutput(co);
                            TuiSessionContext.App.Invoke(() => AppendConvo(
                                $"\n─── compacted ({co.TokensBefore:N0}→{co.TokensAfter:N0} tokens) ───\n\n",
                                Palette.Dim));
                            break;

                        case TurnComplete tc2:
                            StopStreamCursor();
                            VerboseOutput(tc2);
                            if (assistantWritten)
                            {
                                mdRenderer?.Flush();
                                mdRenderer = null;
                                TuiSessionContext.App.Invoke(() => AppendConvo("\n\n", Palette.Normal));
                                assistantWritten = false;
                            }
                            if (thinkingWritten)
                            {
                                TuiSessionContext.App.Invoke(() => AppendConvo("\n", Palette.Normal));
                                thinkingWritten = false;
                            }
                            UpdateStatusBarFromCtx();
                            if (tc2.AnyWriteTools) RefreshChangedFiles();
                            currentTurn++;
                            {
                                var firstUserText = loopCtx.Messages
                                    .OfType<UserMessage>()
                                    .SelectMany(m => m.Content.OfType<TextBlock>())
                                    .Select(tb => tb.Text)
                                    .FirstOrDefault() ?? "";
                                var sessionTitle = firstUserText.Length > 50
                                    ? firstUserText[..50] + "…"
                                    : firstUserText;
                                TuiSessionContext.Session?.UpdateIndex(sessionTitle, cwd, TuiSessionContext.Config.Model.ActiveModel.Id);
                            }
                            break;

                        case RetryScheduled rs:
                            StopStreamCursor();
                            VerboseOutput(rs);
                            TuiSessionContext.App.Invoke(() => statusBar.SetState(
                                $"⏳ retrying in {rs.DelaySeconds}s · attempt {rs.AttemptNumber}/{rs.MaxAttempts}"));
                            break;

                        case ReflectionOccurred ro:
                            StopStreamCursor();
                            VerboseOutput(ro);
                            TuiSessionContext.App.Invoke(() =>
                                AppendConvo($"\n[reflection: {ro.Error}]\n\n", Palette.Warn));
                            break;

                        case AutoContinued ac:
                            StopStreamCursor();
                            VerboseOutput(ac);
                            TuiSessionContext.App.Invoke(() =>
                                AppendConvo(
                                    $"\n[auto-continue {ac.Attempt}/{ac.MaxAttempts} — recovering from {ac.Reason}]\n\n",
                                    Palette.Warn));
                            break;

                        case LoopEnded le:
                            {
                                StopStreamCursor();
                                VerboseOutput(le);
                                if (assistantWritten)
                                {
                                    mdRenderer?.Flush();
                                    mdRenderer = null;
                                    TuiSessionContext.App.Invoke(() => AppendConvo("\n\n", Palette.Normal));
                                }
                                if (thinkingWritten)
                                {
                                    TuiSessionContext.App.Invoke(() => AppendConvo("\n", Palette.Normal));
                                    thinkingWritten = false;
                                }
                                if (le.Reason == EndReason.Error && !string.IsNullOrEmpty(le.Message))
                                    TuiSessionContext.App.Invoke(() => AppendConvoError(le.Message));

                                // Surface why the loop stopped unless it was the normal end of the
                                // LLM's text response.
                                var endReason = le.Reason switch
                                {
                                    EndReason.ResponseComplete => null,
                                    EndReason.TaskComplete => "done.",
                                    EndReason.NudgeLimitReached => $"nudge limit reached {TuiSessionContext.Config.Agent.NudgeLimit}",
                                    EndReason.NoProgress => "no progress — the model stopped advancing the task",
                                    EndReason.MaxTokens => "response cut off by the output token limit",
                                    EndReason.Repetition => "stuck repeating the same tool calls",
                                    EndReason.ToolErrorStreak => "too many consecutive tool errors",
                                    EndReason.TurnLimitReached => $"turn limit reached {TuiSessionContext.Config.Agent.MaxTurns}",
                                    EndReason.ContextTooSmall => "context window full",
                                    EndReason.Cancelled => "cancelled",
                                    EndReason.Error => $"error {le.Message ?? ""}",
                                    _ => null
                                };
                                if (endReason is not null)
                                    TuiSessionContext.App.Invoke(() => AppendConvo(
                                        $"\nLoop ended. Reason: {endReason}\n\n", Palette.Warn));

                                TryExportTrajectory(loopCtx, le);
                                var msg = le.Reason switch
                                {
                                    EndReason.TaskComplete => "idle",
                                    EndReason.ResponseComplete => "idle",
                                    EndReason.NudgeLimitReached => "idle",
                                    EndReason.NoProgress => "idle  [no progress]",
                                    EndReason.MaxTokens => "idle  [truncated]",
                                    EndReason.Repetition => "idle  [repetition]",
                                    EndReason.ToolErrorStreak => "idle  [tool errors]",
                                    EndReason.TurnLimitReached => "idle  [turn limit]",
                                    EndReason.Cancelled => "idle  [cancelled]",
                                    EndReason.Error => "idle  [error]",
                                    EndReason.ContextTooSmall => "idle  [context full]",
                                    _ => "idle"
                                };
                                StopSpinner(msg);
                                break;
                            }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                StopSpinner("idle  [cancelled]");
                TuiSessionContext.App.Invoke(() => AppendConvo("\n[cancelled]\n\n", Palette.Warn));
                TryExportTrajectory(loopCtx, new LoopEnded(EndReason.Cancelled));
            }
            catch (Exception ex)
            {
                StopSpinner("idle  [error]");
                TuiSessionContext.App.Invoke(() => AppendConvoError(
                    ex.InnerException is { } inner
                        ? $"{ex.GetType().Name}: {ex.Message}\n{inner.GetType().Name}: {inner.Message}"
                        : $"{ex.GetType().Name}: {ex.Message}"));
            }
            finally
            {
                scenarioCts = null;
                TuiSessionContext.App.Invoke(() => promptInput.SetFocus());
            }
        }, ct);
    }

    private void VerboseOutput(TextChunk tc)
    {
        if (!TuiSessionContext.Config.Tui.Verbose) return;
        VerboseOutput($"Verbose {nameof(TextChunk)}: ", $"{tc.Text}");
    }
    private void VerboseOutput(ThinkingChunk tc)
    {
        if (!TuiSessionContext.Config.Tui.Verbose) return;
        VerboseOutput($"Verbose {nameof(ThinkingChunk)}: ", $"{tc.Text}");
    }
    private void VerboseOutput(ToolStarted tc)
    {
        if (!TuiSessionContext.Config.Tui.Verbose) return;
        VerboseOutput($"Verbose {nameof(ToolStarted)}: ", $"{tc.ToString()}");
    }
    private void VerboseOutput(ToolFinished tc)
    {
        if (!TuiSessionContext.Config.Tui.Verbose) return;
        VerboseOutput($"Verbose {nameof(ToolFinished)}: ", $"{tc.ToString()}");
    }
    private void VerboseOutput(PermissionRequired tc)
    {
        if (!TuiSessionContext.Config.Tui.Verbose) return;
        VerboseOutput($"Verbose {nameof(PermissionRequired)}: ", $"{tc.ToString()}");
    }
    private void VerboseOutput(CompactionOccurred tc)
    {
        if (!TuiSessionContext.Config.Tui.Verbose) return;
        VerboseOutput($"Verbose {nameof(CompactionOccurred)}: ", $"{tc.ToString()}");
    }
    private void VerboseOutput(TurnComplete tc)
    {
        if (!TuiSessionContext.Config.Tui.Verbose) return;
        VerboseOutput($"Verbose {nameof(TurnComplete)}: ", $"{tc.ToString()}");
    }
    private void VerboseOutput(RetryScheduled tc)
    {
        if (!TuiSessionContext.Config.Tui.Verbose) return;
        VerboseOutput($"Verbose {nameof(RetryScheduled)}: ", $"{tc.ToString()}");
    }
    private void VerboseOutput(ReflectionOccurred tc)
    {
        if (!TuiSessionContext.Config.Tui.Verbose) return;
        VerboseOutput($"Verbose {nameof(ReflectionOccurred)}: ", $"{tc.ToString()}");
    }
    private void VerboseOutput(AutoContinued tc)
    {
        if (!TuiSessionContext.Config.Tui.Verbose) return;
        VerboseOutput($"Verbose {nameof(AutoContinued)}: ", $"{tc.ToString()}");
    }
    private void VerboseOutput(LoopEnded tc)
    {
        if (!TuiSessionContext.Config.Tui.Verbose) return;
        VerboseOutput($"Verbose {nameof(LoopEnded)}: ",  $"{tc.ToString()}");
    }

    private void VerboseOutput(string title, string text)
    {
        TuiSessionContext.App.Invoke(() =>
        {
            AppendConvo(title, Palette.Warn, false);
            AppendConvo(text + "\n", Palette.Dim);
        });
    }
}
