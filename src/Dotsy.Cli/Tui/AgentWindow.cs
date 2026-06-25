using Dotsy.Cli.Tui.Approval;
using Dotsy.Cli.Tui.Colors;
using Dotsy.Cli.Tui.FileList;
using Dotsy.Cli.Tui.Renderers;
using Dotsy.Cli.Tui.ToolList;
using Dotsy.Core.Config;
using Dotsy.Core.Git;
using Dotsy.Core.Loop.Data;
using LibGit2Sharp;
using System.Collections.ObjectModel;
using System.Globalization;

namespace Dotsy.Cli.Tui;

public partial class AgentWindow : Window, IDisposable
{
    private const int DefaultLeftPanelWidthPercentage = 70;
    private const int MinPanelWidth = 20;
    private const int SplitResizeStepPercentage = 1;

    #region Controls
    private readonly StatusBar      statusBar;
    private readonly FrameView      leftFrame;
    private readonly View           divider;
    private readonly FrameView      rightFrame;
    private readonly ScrollableText convo;
    private readonly FrameView      fileFrame;
    private readonly FileListView   fileList;
    private readonly ApprovalView   approvalView;
    private readonly Label          promptLabel;
    private readonly MultilineInput promptInput;
    private readonly FrameView      completionFrame;
    private readonly ListView       completionList;
    private int    leftPanelWidthPercentage;
    private int    inputHeight    = 1;
    private string lastInputText  = "";
    private int    lastInputWidth = -1;
    private int    filePanelManualHeight = -1;
    #endregion

    #region Tool call list
    private readonly ToolListView toolCallList;
    private readonly InspectionFrameView inspectFrame;
    private readonly ScrollableText inspectText;
    // Panel that should regain focus when the inspect overlay closes (tool list or file list).
    private View? inspectReturnFocus;

    #region Tool rows
    // Tool calls accumulate across prompts; each prompt's calls share a group id so the panel
    // can bracket them with a half-frame gutter. toolCount is a monotonic row counter.
    private readonly ObservableCollection<ToolRow> toolCallRows = [];
    private volatile int toolCallCount;
    private int toolCallGroupSeq;
    #endregion
    #endregion

    // ── Slash completion ────────────────────────────────────────────────────────────────
    private readonly ObservableCollection<CompletionItem> completionItems = [];

    #region Streaming cursor
    private readonly object streamCursorLock = new();
    private Timer? streamCursorTimer;
    private bool streamCursorActive;
    private bool streamCursorVisible;
    #endregion

    // ── File change rows ────────────────────────────────────────────────────────────────
    private readonly ObservableCollection<FileRow> fileRows = [];

    // ── Conversation (Cell-based for per-segment colour) ────────────────────────────────
    private readonly List<List<Cell>> conversationLines = [[]];
    // Lines at these indices must not be word-wrapped (diff lines with background padding)
    private readonly HashSet<int> noWrapLineIndices = [];
    private int convoWrapWidth;


    #region Command history
    private readonly List<string> promptHistory  = [];
    private int?   promptHistoryIdx = null;  // null = not navigating; index into history otherwise
    private string promptDraft = "";         // prompt text saved when history navigation started
    #endregion

    #region Scenario
    private CancellationTokenSource? scenarioCts;
    private CancellationTokenSource? splitStatusCts;
    private string? splitStatusRestoreState;
    private bool loaded;
    #endregion

    // ── Spinner ────────────────────────────────────────────────────────────────────────
    private readonly ProgressSpinner spinner;

    private readonly PanelNavigator panelNavigator;

    // ── Constructor ─────────────────────────────────────────────────────────────────────
    public AgentWindow()
    {
        X = 0; Y = 0;
        Width  = Dim.Fill();
        Height = Dim.Fill();
        Border!.Thickness = new Thickness(0, 0, 0, 0);
        SetScheme(Palette.Scheme());
        leftPanelWidthPercentage = NormalizeLeftPanelWidthPercentage(
            TuiSessionContext.Config.Tui.LeftPanelWidthPercentage);
        TuiSessionContext.Config.Tui.LeftPanelWidthPercentage = leftPanelWidthPercentage;

        // Status bar
        statusBar = new StatusBar();

        // Left: conversation (Tab-focusable, scroll only)
        leftFrame = new FrameView
        {
            X = 0, Y = 1, Width = Dim.Percent(leftPanelWidthPercentage), Height = Dim.Fill(1),
            Title = " Conversation "
        };
        leftFrame.SetScheme(Palette.Scheme());
        leftFrame.Border!.Thickness = new Thickness(0, 1, 0, 1); // no left or right bar
        convo = new ScrollableText
        {
            X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(),
            EnableSelectionCopy = true
        };
        fileFrame = new FrameView
        {
            X = 0, Y = Pos.AnchorEnd(3), Width = Dim.Fill(), Height = 3,
            Title = " changed files ", Visible = false
        };
        fileFrame.SetScheme(Palette.Scheme());
        fileFrame.Border!.Thickness = new Thickness(0, 1, 0, 0);
        fileList = new FileListView
        {
            X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(),
             CanFocus = true // enabled only when frame is visible
        };
        fileList.SetScheme(Palette.Scheme());
        fileList.SetSource(fileRows);
        fileList.RowGetter        =  idx => idx < fileRows.Count ? fileRows[idx] : null;
        fileList.KeyDown += (_, key) =>
        {
            if (key.KeyCode == KeyCode.Enter)
            {
                var idx = fileList.SelectedItem;
                if (idx is >= 0 && idx < fileRows.Count)
                    OnFileSelected(fileList, new ListViewItemEventArgs(idx, fileRows[idx.Value]));
                key.Handled = true;
            }
        };
        fileFrame.Add(fileList);
        leftFrame.Add(convo);

        convo.FrameChanged += (_, _) =>
        {
            int newWidth = convo.Frame.Width;
            if (newWidth > 0 && newWidth != convoWrapWidth)
            {
                convoWrapWidth = newWidth;
                TuiSessionContext.App.Invoke(ReloadConvo);
            }
        };

        // TextView (base of ScrollableText) consumes printable keys via InvokeCommands,
        // so they never reach AgentWindow.OnKeyDown. Intercept here and redirect to _input.
        convo.KeyDown += (_, key) =>
        {
            if (IsOverlayVisible()) return;
            if (!IsPrintableChar(key)) return;
            key.Handled = true;
            promptInput!.SetFocus();
            promptInput!.InsertText(key.AsRune.ToString());
        };

        fileList.KeyDown += (_, key) =>
        {
            if (IsOverlayVisible()) return;
            if (!IsPrintableChar(key)) return;
            key.Handled = true;
            promptInput!.SetFocus();
            promptInput!.InsertText(key.AsRune.ToString());
        };

        // Explicit 1-column divider: draws â”¬ at top, â”‚ in middle, â”´ at bottom so the
        // T-junction characters are always correct regardless of LineCanvas corner merging.
        divider = new PaneDivider
        {
            X = Pos.Right(leftFrame), Y = 1, Width = 1, Height = Dim.Fill(1),
            CanFocus = false
        };
        divider.SetScheme(Palette.Scheme());

        // Right: tool call list (Tab-focusable, arrow-key selection)
        rightFrame = new FrameView
        {
            X = Pos.Right(divider), Y = 1, Width = Dim.Fill(), Height = Dim.Fill(1),
            Title = " Tools "
        };
        rightFrame.SetScheme(Palette.Scheme());
        rightFrame.Border!.Thickness = new Thickness(0, 1, 0, 1); // no left or right bar
        toolCallList = new ToolListView
        {
            X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(),
            CanFocus = true
        };
        toolCallList.SetScheme(Palette.Scheme());
        toolCallList.SetSource(toolCallRows);
        toolCallList.RowGetter         = idx => idx >= 0 && idx < toolCallRows.Count ? toolCallRows[idx] : null;
        toolCallList.RowRender        += OnToolRowRender;
        toolCallList.KeyDown += (_, key) =>
        {
            if (key.KeyCode == KeyCode.Enter)
            {
                var idx = toolCallList.SelectedItem;
                if (idx is >= 0 && idx < toolCallRows.Count)
                    OnToolSelected(toolCallList, new ListViewItemEventArgs(idx, toolCallRows[idx.Value]));
                key.Handled = true;
            }
        };
        toolCallList.KeyDown += (_, key) =>
        {
            if (IsOverlayVisible()) return;
            if (!IsPrintableChar(key)) return;
            key.Handled = true;
            promptInput!.SetFocus();
            promptInput!.InsertText(key.AsRune.ToString());
        };
        rightFrame.Add(toolCallList, fileFrame);

        approvalView = new ApprovalView
        {
            X = 0, Y = Pos.AnchorEnd(6), Width = Dim.Fill(), Height = 6
        };
        // Inspect overlay: full-width, drawn on top of both panels
        inspectFrame = new InspectionFrameView
        {
            X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(),
            Title = " Inspection  [Esc close] ", Visible = false
        };
        inspectFrame.SetScheme(Palette.FocusedPanelScheme());
        inspectFrame.Border!.Thickness = new Thickness(0, 1, 0, 0); // title on top, no side or bottom bars
        inspectText = new ScrollableText
        {
            X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(),
            EnableSelectionCopy = true,
            ShowBottomBorder = true
        };
        inspectText.SetScheme(Palette.ReadOnlyTextScheme());
        inspectFrame.Add(inspectText);

        // Input bar: expands vertically as text wraps (up to 20 % of screen)
        promptLabel = new Label
        {
            X = 0, Y = Pos.AnchorEnd(1), Width = 2, Height = 1, Text = "> ",
        };
        promptLabel.SetScheme(Palette.Scheme());
        promptInput = new MultilineInput
        {
            X = 2, Y = Pos.AnchorEnd(1), Width = Dim.Fill(2), Height = 1,
        };
        approvalView.ApprovalClosed += (_, _) =>
        {
            promptLabel.Visible = true;
            promptInput.Visible = true;
            promptInput.SetFocus();
        };
        promptInput.Submitted        += OnInputSubmitted;
        promptInput.ContentsChanged  += (_, _) =>
        {
            ResizeInput();
            UpdateCompletionPopup();
        };
        promptInput.HistoryPrev      += OnHistoryPrev;
        promptInput.HistoryNext      += OnHistoryNext;
        promptInput.QuitRequested    += (_, _) => TuiSessionContext.App.RequestStop();
        promptInput.CancelRequested  += (_, _) => scenarioCts?.Cancel();

        completionFrame = new FrameView
        {
            X = 2, Y = Pos.AnchorEnd(8), Width = 42, Height = 7,
            Title = " complete ", Visible = false
        };
        completionFrame.SetScheme(Palette.Scheme());
        completionList = new ListView
        {
            X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(),
            CanFocus = true
        };
        completionList.SetScheme(Palette.Scheme());
        completionList.SetSource(completionItems);
        completionList.KeyDown += (_, key) =>
        {
            if (key.KeyCode == KeyCode.Enter)
            {
                ApplySelectedCompletion();
                key.Handled = true;
            }
        };
        completionFrame.Add(completionList);

        // _input first so it receives initial focus; inspect overlay last so it renders on top
        Add(statusBar, promptLabel, promptInput, leftFrame, divider, rightFrame, completionFrame, approvalView, inspectFrame);

        spinner = new ProgressSpinner(frame =>
            TuiSessionContext.App.Invoke(() => statusBar.SetSpinnerFrame(frame)));

        IsRunningChanged += (_, args) =>
        {
            if (args.Value)
                InitializeAfterRunStarts();
        };

        panelNavigator = new PanelNavigator(new(convo), new(toolCallList), new(fileFrame, fileList), new(promptInput));
    }

    private void InitializeAfterRunStarts()
    {
        if (loaded) return;
        loaded = true;

        var config = TuiSessionContext.Config;
        statusBar.SetModel(config.Model.ActiveModelId);
        statusBar.SetSession(TuiSessionContext.LoopCtx?.SessionId ?? "");
        TuiSessionContext.StatusUpdate = SetStatus;

        EnsureInitialLayout();
        PrintInitialMessage();
        if (TuiSessionContext.StartupLoadedSession is { } loadedSession)
            RenderLoadedSession(loadedSession);
        promptInput.SetFocus();

        TuiSessionContext.App.Invoke(() =>
        {
            promptInput.SetFocus();
            promptInput.PositionCursor();
        });

        if (TuiSessionContext.App.Navigation is { } nav)
            nav.FocusedChanged += (_, _) => TuiSessionContext.App.Invoke(HighlightFrameBorders);
    }

    private void PrintInitialMessage()
    {
        AppendConvo("dotsy  -  ready\n\n", Palette.Dim);
        AppendConvo("Type a message to start, or /help for commands.\n\n", Palette.Dim);
        AppendConvo("Keyboard commands:\n", Palette.Dim);

        int padding = 12;
        foreach (var (key, description) in new List<(string, string)>() { 
            ("Tab", "move between panels"),
            ("Alt+Arrows", "resize panels"),
            ("Esc", "focus input or close inspection"),
            ("Ctrl+C", "cancel the current agent turn"),
            ("Ctrl+Q", "quit")
        })
        {
            AppendConvo(key.PadRight(padding), Palette.Bright);
            AppendConvo(description + "\n", Palette.Cmd);
        }
        AppendConvo("\n", Palette.Normal);
        foreach (var message in TuiSessionContext.StartupMessages)
            AppendConvo(message + "\n", message.StartsWith("[warn]", StringComparison.Ordinal) ? Palette.Warn : Palette.Dim);
        if (TuiSessionContext.StartupMessages.Count > 0)
            AppendConvo("\n", Palette.Normal);
    }

    private void EnsureInitialLayout()
    {
        if (convo.Frame.Width <= 0)
            TuiSessionContext.App.LayoutAndDraw(false);

        if (convo.Frame.Width > 0)
            convoWrapWidth = convo.Frame.Width;
    }

    private void HighlightFrameBorders()
    {
        var focused = TuiSessionContext.App.Navigation?.GetFocused();
        // Each panel frame brightens only while its own content has focus. The changed-files panel
        // lives inside rightFrame, so "Tools" highlights for the tool list specifically (excluding
        // the file list) and the changed-files frame highlights independently like the other panels.
        bool toolsFocused = IsDescendant(focused, rightFrame) && !IsDescendant(focused, fileFrame);
        leftFrame.SetScheme(IsDescendant(focused, leftFrame) ? Palette.FocusedPanelScheme() : Palette.Scheme());
        rightFrame.SetScheme(toolsFocused ? Palette.FocusedPanelScheme() : Palette.Scheme());
        fileFrame.SetScheme(IsDescendant(focused, fileFrame) ? Palette.FocusedPanelScheme() : Palette.Scheme());
        leftFrame.SetNeedsDraw();
        rightFrame.SetNeedsDraw();
        fileFrame.SetNeedsDraw();
    }

    private static bool IsDescendant(View? v, View ancestor)
    {
        while (v is not null)
        {
            if (v == ancestor) return true;
            v = v.SuperView;
        }
        return false;
    }

    /// <summary>
    /// Returns true if either the approval overlay or the inspect overlay is currently visible.
    /// This check is used to prevent certain key events (like Enter) from triggering actions in 
    /// the underlying panels when an overlay is active. 
    /// For example accepting an approval or inspecting a tool call should not also trigger the default action of the focused panel.
    /// </summary>
    /// <returns>true if overlay is visible; otherwise, false.</returns>
    protected bool IsOverlayVisible() => approvalView.Visible || inspectFrame.Visible;

    protected override bool OnKeyDown(Key key)
    {
        // Ctrl+C cancels the running agent regardless of which panel is focused.
        // Ctrl+Q quit is handled via _input.QuitRequested when the input has focus;
        // also handle it here so it works when focus is on the conversation/tool panels.
        // key.KeyCode for modifier combos includes the mask (Key.Q.WithCtrl not KeyCode.Q).
        if (key == new Key(KeyCode.C).WithCtrl)
        {
            scenarioCts?.Cancel();
            return true;
        }

        if (key == new Key(KeyCode.Q).WithCtrl)
        {
            TuiSessionContext.App.RequestStop();
            return true;
        }

        if (key.KeyCode == KeyCode.Esc && inspectFrame.Visible)
        {
            HideInspect();
            return true;
        }

        // The approval overlay is modal-ish: trap Tab/Shift+Tab so focus cycles its buttons and
        // never escapes to the (hidden) entry field or the panels underneath it.
        if (approvalView.Visible)
        {
            if (key.KeyCode == KeyCode.Esc)
            {
                approvalView.FocusFirstButton();
                return true;
            }

            if (key == Key.Tab || key == Key.Tab.WithShift)
            {
                approvalView.CycleFocus(back: key == Key.Tab.WithShift);
                return true;
            }
        }

        if (completionFrame.Visible)
        {
            switch (key.KeyCode)
            {
                case KeyCode.Esc:
                    HideCompletionPopup();
                    promptInput.SetFocus();
                    return true;
                case KeyCode.Tab:
                case KeyCode.Enter:
                    ApplySelectedCompletion();
                    return true;
                case KeyCode.CursorUp:
                    MoveCompletionSelection(-1);
                    return true;
                case KeyCode.CursorDown:
                    MoveCompletionSelection(1);
                    return true;
            }
        }

        // Escape on main page: focus the command line input (don't exit app)
        if (key.KeyCode == KeyCode.Esc && !IsOverlayVisible())
        {
            TuiSessionContext.App.Invoke(() => promptInput.SetFocus());
            return true;
        }

        // Adjust split with Alt+Left/Right when focus is in either panel or the divider (but not the input or approval/inspect overlays)
        var foc = TuiSessionContext.App.Navigation?.GetFocused();
        if ((key == Key.CursorLeft.WithAlt || key == Key.CursorRight.WithAlt)
            && TryResizePanelSplit(key, foc))
            return true;

        if ((key == Key.CursorUp.WithAlt || key == Key.CursorDown.WithAlt)
            && TryResizeFilePanelSplit(key, foc))
            return true;
        // Prevent boundary navigation from stealing focus away from content panels
        if (foc == convo || foc == toolCallList || foc == fileList)
        {
            switch (key.KeyCode)
            {
                case KeyCode.CursorUp:
                case KeyCode.CursorDown:
                case KeyCode.PageUp:
                case KeyCode.PageDown:
                case KeyCode.Home:
                case KeyCode.End:
                case KeyCode.CursorLeft:
                case KeyCode.CursorRight:
                    return true; // consume: view already handled it; don't let Toplevel move focus
            }
        }

        bool plainTab = key == Key.Tab;
        bool shiftTab = key == Key.Tab.WithShift;
        if ((plainTab || shiftTab) && !IsOverlayVisible())
        {
            var focused = TuiSessionContext.App.Navigation?.GetFocused();
            
            if (plainTab && focused == promptInput && TryShowCompletionPopup())
                return true;

            View next = panelNavigator.Next(focused, back: shiftTab);
            TuiSessionContext.App.Invoke(() => next.SetFocus());
            return true; // always consume: never let Toplevel base run Tab navigation
        }

        // Typing a printable character outside the input redirects focus there.
        // We insert the char directly because returning false after SetFocus does
        // not re-route the key to the newly focused view in TG v2's routing model.
        if (!IsOverlayVisible() && IsPrintableChar(key))
        {
            var focused = TuiSessionContext.App.Navigation?.GetFocused();
            if (focused != promptInput)
            {
                promptInput.SetFocus();
                promptInput.InsertText(key.AsRune.ToString());
                return true;
            }
        }

        return base.OnKeyDown(key);
    }

    private static bool IsPrintableChar(Key key)
    {
        if (key.IsCtrl || key.IsAlt) return false;
        // Letters Aâ€“Z carry ShiftMask in KeyCode; strip it before the range check.
        var kc = (uint)(key.KeyCode & ~KeyCode.ShiftMask);
        return kc >= 32 && kc <= 126; // ASCII printable range (space â€¦ ~)
    }

    #region Split resizing
    private bool TryResizePanelSplit(Key key, View? focused)
    {
        bool leftFocused = focused == convo;
        bool rightFocused = IsDescendant(focused, rightFrame);
        if (!leftFocused && !rightFocused)
            return false;

        int delta = key == Key.CursorRight.WithAlt ? SplitResizeStepPercentage : -SplitResizeStepPercentage;
        SetLeftPanelWidthPercentage(leftPanelWidthPercentage + delta, persist: true);
        return true;
    }

    private void SetLeftPanelWidthPercentage(int percentage, bool persist)
    {
        var clamped = ClampLeftPanelWidthPercentage(
            NormalizeLeftPanelWidthPercentage(percentage),
            GetLayoutWidth());
        if (clamped == leftPanelWidthPercentage)
            return;

        leftPanelWidthPercentage = clamped;
        TuiSessionContext.Config.Tui.LeftPanelWidthPercentage = clamped;
        leftFrame.Width = Dim.Percent(clamped);
        SetNeedsLayout();
        SetNeedsDraw();
        ReloadConvo();

        if (!persist)
            return;

        var (ok, msg) = ConfigEditor.Set(
            TuiSessionContext.Config,
            "tui.left-panel-width-percentage",
            clamped.ToString(CultureInfo.InvariantCulture));
        if (ok)
            ShowTemporarySplitStatus($"split {clamped}%");
        else
            statusBar.SetState($"split not saved: {msg}");
    }

    private void ShowTemporarySplitStatus(string message)
    {
        splitStatusRestoreState ??= statusBar.State;
        splitStatusCts?.Cancel();
        splitStatusCts?.Dispose();
        var cts = new CancellationTokenSource();
        splitStatusCts = cts;
        var restoreState = splitStatusRestoreState;

        statusBar.SetState(message);

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), cts.Token);
                TuiSessionContext.App.Invoke(() =>
                {
                    if (!cts.IsCancellationRequested && ReferenceEquals(splitStatusCts, cts))
                    {
                        splitStatusCts = null;
                        splitStatusRestoreState = null;
                        statusBar.SetState(restoreState ?? "idle");
                    }
                });
            }
            catch (OperationCanceledException) { }
        });
    }

    private static int NormalizeLeftPanelWidthPercentage(int percentage) =>
        percentage is > 0 and < 100 ? percentage : DefaultLeftPanelWidthPercentage;

    private int GetLayoutWidth() =>
        Frame.Width > 0 ? Frame.Width : TuiSessionContext.App.Driver?.Cols ?? 0;

    private static int ClampLeftPanelWidthPercentage(int percentage, int totalWidth)
    {
        if (totalWidth <= 0 || totalWidth < MinPanelWidth * 2 + 1)
            return Math.Clamp(percentage, 1, 99);

        int minPct = (int)Math.Ceiling(MinPanelWidth * 100.0 / totalWidth);
        int maxPct = (int)Math.Floor((totalWidth - MinPanelWidth - 1) * 100.0 / totalWidth);
        return Math.Clamp(percentage, minPct, maxPct);
    }
    private bool TryResizeFilePanelSplit(Key key, View? focused)
    {
        if (!IsDescendant(focused, rightFrame))
            return false;

        // The changed-files panel is anchored at the bottom, so growing it moves the divider up.
        // Alt+Up therefore enlarges it (divider up) and Alt+Down shrinks it (divider down).
        int delta = key == Key.CursorUp.WithAlt ? 1 : -1;
        if (filePanelManualHeight == -1)
        {
            int currentH = Math.Clamp(fileRows.Count + 2, 3, (int)(Math.Max(10, rightFrame.Frame.Height - 2) * 0.30));
            filePanelManualHeight = currentH;
        }

        filePanelManualHeight = Math.Clamp(filePanelManualHeight + delta, 3, rightFrame.Frame.Height - 2);
        UpdateFilePanel();
        return true;
    }
    #endregion

    #region File panel

    public void AddFileDiff(string path, int added, int deleted, FileChangeType changeType, List<List<Cell>> diff) =>
        TuiSessionContext.App.Invoke(() =>
        {
            fileRows.Add(new FileRow(path, added, deleted, changeType, diff));
            UpdateFilePanel();
        });

    private void UpdateFilePanel()
    {
        int total = fileRows.Count;
        int innerH = Math.Max(10, rightFrame.Frame.Height - 2);
        int maxH   = Math.Max(3, (int)(innerH * 0.30));
        int wantH  = total + 2; 

        int h = filePanelManualHeight != -1 
            ? Math.Clamp(filePanelManualHeight, 3, innerH) 
            : Math.Clamp(wantH, 3, maxH);

        int addedFiles   = fileRows.Count(r => r.ChangeType == FileChangeType.Added);
        int deletedFiles = fileRows.Count(r => r.ChangeType == FileChangeType.Deleted);
        fileFrame.Title = $" changed files ({total}, +{addedFiles} -{deletedFiles}) ";

        fileFrame.Height  = h;
        fileFrame.Y       = Pos.AnchorEnd(h);
        fileFrame.Visible = true;
        toolCallList.Height = Dim.Fill(h);
        rightFrame.SetNeedsDraw();
    }

    private void RefreshChangedFiles()
    {
        var cwd = TuiSessionContext.Cwd;
        Task.Run(() =>
        {
            // Build the rows (git diff + cell rendering) off the UI thread; only the swap is marshalled.
            var rows = BuildFileRows(cwd);
            TuiSessionContext.App.Invoke(() =>
            {
                fileRows.Clear();
                foreach (var row in rows) fileRows.Add(row);
                UpdateFilePanel();
            });
        });
    }

    // Resolves each changed file's line counts and a colorized unified diff (for the inspect view).
    // The HEAD→working-tree patch is computed once; untracked files (absent from that patch) are
    // rendered from their on-disk content as all-additions.
    private static List<FileRow> BuildFileRows(string cwd)
    {
        var files = GitContext.GetChangedFiles(cwd);
        var rows = new List<FileRow>(files.Count);

        LibGit2Sharp.Patch? patch = null;
        try
        {
            var repoPath = LibGit2Sharp.Repository.Discover(cwd);
            if (repoPath != null)
            {
                using var repo = new LibGit2Sharp.Repository(repoPath);
                if (repo.Head.Tip is { } tip)
                    patch = repo.Diff.Compare<LibGit2Sharp.Patch>(tip.Tree, LibGit2Sharp.DiffTargets.WorkingDirectory);
            }
        }
        catch { }

        foreach (var f in files)
        {
            var ct = f.IsNew ? FileChangeType.Added
                       : f.IsDeleted ? FileChangeType.Deleted
                       : FileChangeType.Modified;

            int added = 0, deleted = 0;
            List<List<Cell>> diff = [];

            var entry = patch?.FirstOrDefault(c => c.Path.Equals(f.Path, StringComparison.OrdinalIgnoreCase));
            if (entry is not null)
            {
                added = entry.LinesAdded;
                deleted = entry.LinesDeleted;
                diff = RenderUnifiedDiff(entry.Patch);
            }
            else if (f.IsNew)
            {
                diff = RenderNewFileAsAdditions(cwd, f.Path, out added);
            }

            rows.Add(new FileRow(f.Path, added, deleted, ct, diff));
        }

        return rows;
    }

    // Colorizes a unified-diff patch into renderable cell rows: +/- lines in success/error colors,
    // hunk headers highlighted, file headers and the "no newline" marker dimmed, context in the
    // diff-context color. Foreground-only colors so it reads cleanly in the full-width inspect panel.
    private static List<List<Cell>> RenderUnifiedDiff(string patchText)
    {
        var lines = new List<List<Cell>>();
        if (string.IsNullOrEmpty(patchText)) return lines;

        foreach (var raw in patchText.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            lines.Add(TextToCells("  " + line, DiffRenderer.Color(DiffRenderer.Classify(line))));
        }

        // Split leaves a trailing empty element when the patch ends with a newline.
        if (lines.Count > 0 && lines[^1].Count == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    // Renders an untracked file's content as an all-additions diff. Guards against large and binary
    // files so the inspect panel never tries to render megabytes of (or non-text) data.
    private static List<List<Cell>> RenderNewFileAsAdditions(string cwd, string path, out int added)
    {
        added = 0;
        var lines = new List<List<Cell>>();
        try
        {
            var full = Path.IsPathRooted(path) ? path : Path.Combine(cwd, path);
            if (!File.Exists(full)) return lines;

            var info = new FileInfo(full);
            if (info.Length > 512 * 1024 || LooksBinary(full))
            {
                lines.Add(TextToCells("  (new file — preview unavailable)", Palette.Dim));
                return lines;
            }

            foreach (var raw in File.ReadLines(full))
            {
                lines.Add(TextToCells("  +" + raw.TrimEnd('\r'), Palette.Success));
                added++;
            }
        }
        catch { }
        return lines;
    }

    private static bool LooksBinary(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> buf = stackalloc byte[8000];
            int n = fs.Read(buf);
            for (int i = 0; i < n; i++)
                if (buf[i] == 0) return true;
            return false;
        }
        catch { return true; }
    }

    private void OnFileRowRender(object? sender, ListViewRowEventArgs e)
    {
        if (e.Row >= fileRows.Count) return;
        var selected = fileList.HasFocus && fileList.SelectedItem == e.Row;
        e.RowAttribute = selected
            ? Palette.SelRow
            : Palette.Normal;
    }

    private void OnFileSelected(object? sender, ListViewItemEventArgs e)
    {
        if (IsOverlayVisible()) return;
        if (e.Value is FileRow row) ShowFileDiff(row);
    }

    private void ShowFileDiff(FileRow row)
    {
        var lines = new List<List<Cell>>();
        void AddLine(string text, TGAttribute attr) => lines.Add(TextToCells(text, attr));
        var typeLabel = row.ChangeType switch
        {
            FileChangeType.Added => "new file",
            FileChangeType.Deleted => "deleted",
            _ => "modified"
        };
        AddLine($"  {row.Path}  [{typeLabel}]", Palette.Bright);
        lines.Add([]);
        AddLine($"  +{row.Added} added  -{row.Deleted} deleted", Palette.Normal);
        lines.Add([]);
        lines.AddRange(row.Diff);
        inspectText.LoadText(lines);
        inspectFrame.Title = $" {row.Path}  [Esc close] ";
        inspectReturnFocus = fileList;
        ShowInspectFrame();
    }
    #endregion

    #region Size and wrapping
    private void ResizeInput()
    {
        var text = promptInput.Text?.ToString() ?? "";
        // ContentsChanged also fires on cursor movement; skip resize when text is unchanged.
        var width = Math.Max(1, promptInput.Frame.Width > 0 ? promptInput.Frame.Width : TuiSessionContext.App.Driver?.Cols - 4 ?? 20);
        if (text == lastInputText && width == lastInputWidth) return;
        lastInputText = text;
        lastInputWidth = width;

        int rows = TuiSessionContext.App.Driver?.Rows ?? 24;

        int lines = CountWrappedLines(text, width);
        int maxH  = Math.Max(1, (int)(rows * 0.20));
        int h     = Math.Clamp(lines, 1, maxH);

        if (h == inputHeight) return;
        inputHeight = h;

        promptInput.Height  = Dim.Absolute(h);
        promptInput.Y       = Pos.AnchorEnd(h);
        promptLabel.Y       = Pos.AnchorEnd(h);
        leftFrame.Height    = Dim.Fill(h);
        rightFrame.Height   = Dim.Fill(h);
        divider.Height      = Dim.Fill(h);
        approvalView.Y      = Pos.AnchorEnd(5 + h);
        completionFrame.Y   = Pos.AnchorEnd(h + Math.Max(3, completionFrame.Frame.Height));
        convo.MoveEnd();
        SetNeedsDraw();
    }

    private static int CountWrappedLines(string text, int width)
    {
        var normalized = text.TrimEnd('\n', '\r');
        if (normalized.Length == 0) return 1;

        var count = 0;
        foreach (var rawLine in normalized.Split('\n'))
            count += CountWrappedLine(rawLine.TrimEnd('\r'), width);
        return Math.Max(1, count);
    }

    private static int CountWrappedLine(string line, int width)
    {
        if (line.Length == 0) return 1;

        var rows = 1;
        var col = 0;
        foreach (var word in line.Split(' '))
        {
            var wordLen = word.Length;
            var space = col > 0 ? 1 : 0;
            if (col > 0 && col + space + wordLen > width)
            {
                rows++;
                col = 0;
                space = 0;
            }

            var total = space + wordLen;
            if (wordLen > width)
            {
                rows += (wordLen - Math.Max(0, width - col) + width - 1) / width;
                col = wordLen % width;
                if (col == 0) col = width;
            }
            else
            {
                col += total;
            }
        }
        return rows;
    }

    private static List<string> WordWrap(string text, int width)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var word in text.Split(' '))
        {
            if (current.Length == 0)
            {
                current.Append(word);
            }
            else if (current.Length + 1 + word.Length <= width)
            {
                current.Append(' ');
                current.Append(word);
            }
            else
            {
                result.Add(current.ToString());
                current.Clear();
                current.Append(word);
            }
        }
        if (current.Length > 0)
            result.Add(current.ToString());
        return result.Count > 0 ? result : [""];
    }
    #endregion

    #region Status bar
    public void SetStatus(string state) => TuiSessionContext.App.Invoke(() =>
    {
        splitStatusCts?.Cancel();
        splitStatusCts?.Dispose();
        splitStatusCts = null;
        splitStatusRestoreState = null;
        statusBar.SetState(state);
    });

    public void StartSpinner(string state)
    {
        SetStatus(state);
        spinner.Start();
    }

    public void StopSpinner(string state)
    {
        spinner.Stop();
        SetStatus(state);
    }

    private void UpdateStatusBarFromCtx()
    {
        var ctx = TuiSessionContext.LoopCtx;
        var config = TuiSessionContext.Config;
        if (ctx is null) return;
        TuiSessionContext.App.Invoke(() =>
        {
            statusBar.SetModel(config.Model.ActiveModelId);
            statusBar.SetCtxPct(ctx.TokenBudget.UsagePct);
        });
    }
    #endregion

    #region Streaming cursor
    private void StartStreamCursor()
    {
        lock (streamCursorLock)
        {
            streamCursorActive = true;
            streamCursorTimer?.Dispose();
            streamCursorTimer = new System.Threading.Timer(
                _ => TuiSessionContext.App.Invoke(ToggleStreamCursor),
                null, 500, 500);
        }
        ShowStreamCursor();
    }

    private void StopStreamCursor()
    {
        lock (streamCursorLock)
        {
            streamCursorActive = false;
            streamCursorTimer?.Dispose();
            streamCursorTimer = null;
        }
        HideStreamCursor();
    }

    private void ToggleStreamCursor()
    {
        lock (streamCursorLock)
        {
            if (!streamCursorActive) return;
        }

        if (streamCursorVisible)
            HideStreamCursor();
        else
            ShowStreamCursor();
    }

    private void ShowStreamCursor()
    {
        TuiSessionContext.App.Invoke(() =>
        {
            lock (streamCursorLock)
            {
                if (!streamCursorActive || streamCursorVisible) return;
                streamCursorVisible = true;
            }
            ReloadConvo();
        });
    }

    private void HideStreamCursor()
    {
        TuiSessionContext.App.Invoke(() =>
        {
            lock (streamCursorLock)
            {
                if (!streamCursorVisible) return;
                streamCursorVisible = false;
            }
            ReloadConvo();
        });
    }
    #endregion


    #region Input/output handling and prompt management
    private void OnHistoryPrev(object? sender, EventArgs e)
    {
        if (promptHistory.Count == 0) return;
        if (!promptHistoryIdx.HasValue)
        {
            promptDraft = promptInput.Text?.ToString() ?? "";
            promptHistoryIdx = promptHistory.Count - 1;
        }
        else if (promptHistoryIdx > 0)
        {
            promptHistoryIdx--;
        }
        TuiSessionContext.App.Invoke(() => { promptInput.Text = promptHistory[promptHistoryIdx.Value]; promptInput.MoveEnd(); });
    }

    private void OnHistoryNext(object? sender, EventArgs e)
    {
        if (!promptHistoryIdx.HasValue) return;
        if (promptHistoryIdx < promptHistory.Count - 1)
        {
            promptHistoryIdx++;
            TuiSessionContext.App.Invoke(() => { promptInput.Text = promptHistory[promptHistoryIdx.Value]; promptInput.MoveEnd(); });
        }
        else
        {
            promptHistoryIdx = null;
            var draft = promptDraft;
            TuiSessionContext.App.Invoke(() => { promptInput.Text = draft; promptInput.MoveEnd(); });
        }
    }

    private void OnInputSubmitted(object? sender, string raw)
    {
        var trimmed = raw.Trim();
        if (string.IsNullOrEmpty(trimmed)) return;
        HideCompletionPopup();

        if (!string.IsNullOrEmpty(trimmed) && (promptHistory.Count == 0 || promptHistory[^1] != trimmed))
            promptHistory.Add(trimmed);
        promptHistoryIdx = null;
        promptDraft = "";

        promptInput.Text = "";
        ResizeInput();

        // Slash commands handled synchronously on the UI thread
        if (trimmed.StartsWith('/'))
        {
            HandleSlashCommand(trimmed);
            return;
        }

        SubmitUserPrompt(trimmed, trimmed);
    }
    #endregion

    private void TryExportTrajectory(LoopContext loopCtx, LoopEnded ended)
    {
        try
        {
            TuiSessionContext.Trajectory?.Export(loopCtx, ended.Reason, ended.Message);
        }
        catch (Exception ex)
        {
            TuiSessionContext.App.Invoke(() => AppendConvo($"\n[warn] trajectory export failed: {ex.Message}\n", Palette.Warn));
        }
    }

    // Converts text to colored cells by Unicode scalar (rune), not UTF-16 char. Iterating chars and
    // calling `new Rune(char)` throws ArgumentOutOfRangeException on a surrogate half (astral-plane
    // emoji are surrogate pairs); EnumerateRunes pairs surrogates and substitutes U+FFFD for
    // unpaired ones. Skips CR/LF so callers can pass a whole line.
    private static List<Cell> TextToCells(string text, TGAttribute attr)
    {
        var cells = new List<Cell>();
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value == '\r' || rune.Value == '\n') continue;
            // Skip zero-width runes (variation selectors like U+FE0F, combining marks, ZWJ, control
            // chars): a cell grid can't render them and the stray replacement glyph desyncs the row.
            if (Glyphs.GetColumns(rune) <= 0) continue;
            // Replace emoji-presentation runes (terminal draws 2 cols, TG draws 1 -> grid desync).
            cells.Add(new Cell(attr, false, Glyphs.Safe(rune).ToString()));
        }
        return cells;
    }

    public new void Dispose()
    {
        spinner?.Dispose();
        streamCursorTimer?.Dispose();
        splitStatusCts?.Cancel();
        splitStatusCts?.Dispose();
        scenarioCts?.Cancel();
        scenarioCts?.Dispose();
        base.Dispose();
    }
}
