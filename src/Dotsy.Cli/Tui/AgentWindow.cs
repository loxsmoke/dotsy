using System.Collections.ObjectModel;
using System.Globalization;
using Dotsy.Core.Config;
using Dotsy.Core.Git;
using Dotsy.Core.Loop.Data;
using Terminal.Gui;
using TGAttribute = Terminal.Gui.Attribute;

namespace Dotsy.Cli.Tui;

public partial class AgentWindow : Toplevel, IDisposable
{
    private const int DefaultLeftPanelWidthPercentage = 70;
    private const int MinPanelWidth = 20;
    private const int SplitResizeStepPercentage = 1;

    // ── Controls ──────────────────────────────────────────────────────────────
    private readonly StatusBar      statusBar;
    private readonly FrameView      leftFrame;
    private readonly View           divider;
    private readonly FrameView      rightFrame;
    private readonly ScrollableText convo;
    private readonly FrameView      fileFrame;
    private readonly FileListView   fileList;
    private readonly ToolListView   toolCallList;
    private readonly InspectionFrameView inspectFrame;
    private readonly TextView       inspectText;
    private readonly FrameView      approvalFrame;
    private readonly Label          approvalMsg;
    private FlatButton?             btnProject;
    private readonly Label          promptLabel;
    private readonly MultilineInput promptInput;
    private readonly FrameView      completionFrame;
    private readonly ListView       completionList;
    private int    leftPanelWidthPercentage;
    private int    inputHeight    = 1;
    private string lastInputText  = "";
    private int    lastInputWidth = -1;
    private int    filePanelManualHeight = -1;
    // ── Slash completion ─────────────────────────────────────────────────────
    private readonly ObservableCollection<CompletionItem> completionItems = new();

    // ── Streaming cursor ─────────────────────────────────────────────────────
    private readonly object streamCursorLock = new();
    private System.Threading.Timer? streamCursorTimer;
    private bool streamCursorActive;
    private bool streamCursorVisible;

    // ── File change rows ──────────────────────────────────────────────────────
    private readonly ObservableCollection<FileRow> fileRows = new();

    // ── Conversation (Cell-based for per-segment colour) ──────────────────────
    private readonly List<List<Cell>> conversationLines = [[]];
    // Lines at these indices must not be word-wrapped (diff lines with background padding)
    private readonly HashSet<int> noWrapLineIndices = new();
    private int convoWrapWidth;

    // ── Tool rows ─────────────────────────────────────────────────────────────
    // Tool calls accumulate across prompts; each prompt's calls share a group id so the panel
    // can bracket them with a half-frame gutter. toolCount is a monotonic row counter.
    private readonly ObservableCollection<ToolRow> toolCallRows = new();
    private volatile int toolCallCount;
    private int toolCallGroupSeq;

    // ── Approval ──────────────────────────────────────────────────────────────
    private TaskCompletionSource<ApprovalChoice>? approvalTcs;

    // ── Command history ───────────────────────────────────────────────────────
    private readonly List<string> promptHistory  = new();
    private int?   promptHistoryIdx = null;  // null = not navigating; index into history otherwise
    private string promptDraft = "";         // prompt text saved when history navigation started

    // ── Scenario ──────────────────────────────────────────────────────────────
    private CancellationTokenSource? scenarioCts;
    private CancellationTokenSource? splitStatusCts;
    private string? splitStatusRestoreState;

    // ── Spinner ───────────────────────────────────────────────────────────────
    private readonly ProgressSpinner spinner;

    // ─────────────────────────────────────────────────────────────────────────
    public AgentWindow()
    {
        X = 0; Y = 0;
        Width  = Dim.Fill();
        Height = Dim.Fill();
        ColorScheme = Palette.Scheme();
        leftPanelWidthPercentage = NormalizeLeftPanelWidthPercentage(
            TuiSessionContext.Config.Tui.LeftPanelWidthPercentage);
        TuiSessionContext.Config.Tui.LeftPanelWidthPercentage = leftPanelWidthPercentage;

        // Status bar
        statusBar = new StatusBar();

        // Left: conversation (Tab-focusable, scroll only)
        leftFrame = new FrameView
        {
            X = 0, Y = 1, Width = Dim.Percent(leftPanelWidthPercentage), Height = Dim.Fill(1),
            Title = " Conversation ", ColorScheme = Palette.Scheme()
        };
        leftFrame.Border!.Thickness = new Thickness(0, 1, 0, 1); // no left or right bar
        convo = new ScrollableText
        {
            X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(),
            EnableSelectionCopy = true
        };
        fileFrame = new FrameView
        {
            X = 0, Y = Pos.AnchorEnd(3), Width = Dim.Fill(), Height = 3,
            Title = " changed files ", Visible = false, ColorScheme = Palette.Scheme()
        };
        fileFrame.Border!.Thickness = new Thickness(0, 1, 0, 0);
        fileList = new FileListView
        {
            X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(),
             CanFocus = true, ColorScheme = Palette.Scheme() // enabled only when frame is visible
        };
        fileList.SetSource(fileRows);
        fileList.RowGetter        =  idx => idx < fileRows.Count ? fileRows[idx] : null;
        fileList.OpenSelectedItem += OnFileSelected;
        fileFrame.Add(fileList);
        leftFrame.Add(convo);

        convo.FrameChanged += (_, _) =>
        {
            int newWidth = convo.Frame.Width;
            if (newWidth > 0 && newWidth != convoWrapWidth)
            {
                convoWrapWidth = newWidth;
                Application.Invoke(ReloadConvo);
            }
        };

        // TextView (base of ScrollableText) consumes printable keys via InvokeCommands,
        // so they never reach AgentWindow.OnKeyDown. Intercept here and redirect to _input.
        convo.KeyDown += (_, key) =>
        {
            if (approvalFrame?.Visible == true || inspectFrame?.Visible == true) return;
            if (!IsPrintableChar(key)) return;
            key.Handled = true;
            promptInput!.SetFocus();
            promptInput!.InsertText(key.AsRune.ToString());
        };

        fileList.KeyDown += (_, key) =>
        {
            if (approvalFrame?.Visible == true || inspectFrame?.Visible == true) return;
            if (!IsPrintableChar(key)) return;
            key.Handled = true;
            promptInput!.SetFocus();
            promptInput!.InsertText(key.AsRune.ToString());
        };

        // Explicit 1-column divider: draws ┬ at top, │ in middle, ┴ at bottom so the
        // T-junction characters are always correct regardless of LineCanvas corner merging.
        divider = new PaneDivider
        {
            X = Pos.Right(leftFrame), Y = 1, Width = 1, Height = Dim.Fill(1),
            CanFocus = false, ColorScheme = Palette.Scheme()
        };

        // Right: tool call list (Tab-focusable, arrow-key selection)
        rightFrame = new FrameView
        {
            X = Pos.Right(divider), Y = 1, Width = Dim.Fill(), Height = Dim.Fill(1),
            Title = " Tools ", ColorScheme = Palette.Scheme()
        };
        rightFrame.Border!.Thickness = new Thickness(0, 1, 0, 1); // no left or right bar
        toolCallList = new ToolListView
        {
            X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(),
            CanFocus = true, ColorScheme = Palette.Scheme()
        };
        toolCallList.SetSource(toolCallRows);
        toolCallList.RowGetter         = idx => idx >= 0 && idx < toolCallRows.Count ? toolCallRows[idx] : null;
        toolCallList.RowRender        += OnToolRowRender;
        toolCallList.OpenSelectedItem += OnToolSelected;
        toolCallList.KeyDown += (_, key) =>
        {
            if (approvalFrame?.Visible == true || inspectFrame?.Visible == true) return;
            if (!IsPrintableChar(key)) return;
            key.Handled = true;
            promptInput!.SetFocus();
            promptInput!.InsertText(key.AsRune.ToString());
        };
        rightFrame.Add(toolCallList, fileFrame);

        // Approval overlay (height=6: row 0=msg, row 2=main buttons, row 3=project button)
        approvalFrame = new FrameView
        {
            X = 0, Y = Pos.AnchorEnd(6), Width = Dim.Fill(), Height = 6,
            Title = " Tool approval ", Visible = false, ColorScheme = Palette.Scheme()
        };
        approvalMsg = new Label { X = 2, Y = 0, Width = Dim.Fill(2), Text = "", ColorScheme = Palette.Scheme() };

        var btnOnce   = new FlatButton("Allow once")        { X = 2,                        Y = 2 };
        var btnAlways = new FlatButton("Always allow")      { X = Pos.Right(btnOnce) + 2,   Y = 2 };
        var btnDeny   = new FlatButton("Deny")              { X = Pos.Right(btnAlways) + 2, Y = 2 };
        btnProject   = new FlatButton("Allow for project") { X = 2,                        Y = 3, Visible = false };

        // Add all buttons to the frame
        approvalFrame.Add(approvalMsg, btnOnce, btnAlways, btnDeny, btnProject);

        // Set up button positioning based on available width
        PositionApprovalButtons();

        btnOnce.Fired    += (_, _) => AcceptApproval(ApprovalChoice.AllowOnce);
        btnAlways.Fired  += (_, _) => AcceptApproval(ApprovalChoice.AlwaysAllow);
        btnDeny.Fired    += (_, _) => AcceptApproval(ApprovalChoice.Deny);
        btnProject.Fired += (_, _) => AcceptApproval(ApprovalChoice.AllowForProject);

        // Inspect overlay — full-width, drawn on top of both panels
        inspectFrame = new InspectionFrameView
        {
            X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(1),
            Title = " Inspection  [Ctrl+C copy · Ctrl+A all · Esc close] ", Visible = false, ColorScheme = Palette.Scheme()
        };
        inspectText = new ScrollableText
        {
            X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(),
            ColorScheme = Palette.ReadOnlyTextScheme(),
            ShowScrollBars = false,
            EnableSelectionCopy = true
        };
        inspectFrame.ContentView = inspectText;
        inspectFrame.Add(inspectText);

        // Input bar — expands vertically as text wraps (up to 20 % of screen)
        promptLabel = new Label
        {
            X = 0, Y = Pos.AnchorEnd(1), Width = 2, Height = 1, Text = "> ",
            ColorScheme = Palette.Scheme()
        };
        promptInput = new MultilineInput
        {
            X = 2, Y = Pos.AnchorEnd(1), Width = Dim.Fill(2), Height = 1,
        };
        promptInput.Submitted        += OnInputSubmitted;
        promptInput.ContentsChanged  += (_, _) =>
        {
            ResizeInput();
            UpdateCompletionPopup();
        };
        promptInput.HistoryPrev      += OnHistoryPrev;
        promptInput.HistoryNext      += OnHistoryNext;
        promptInput.QuitRequested    += (_, _) => Application.RequestStop();
        promptInput.CancelRequested  += (_, _) => scenarioCts?.Cancel();

        completionFrame = new FrameView
        {
            X = 2, Y = Pos.AnchorEnd(8), Width = 42, Height = 7,
            Title = " complete ", Visible = false, ColorScheme = Palette.Scheme()
        };
        completionList = new ListView
        {
            X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(),
            CanFocus = true, ColorScheme = Palette.Scheme()
        };
        completionList.SetSource(completionItems);
        completionList.OpenSelectedItem += (_, _) => ApplySelectedCompletion();
        completionFrame.Add(completionList);

        // _input first so it receives initial focus; inspect overlay last so it renders on top
        Add(statusBar, promptLabel, promptInput, leftFrame, divider, rightFrame, completionFrame, approvalFrame, inspectFrame);

        spinner = new ProgressSpinner(frame =>
            Application.Invoke(() => statusBar.SetSpinnerFrame(frame)));
    }

    private void PositionApprovalButtons()
    {
        // Get the available width for buttons (accounting for padding)
        int availableWidth = approvalFrame.Frame.Width - 4; // Subtract 4 for padding (2 on each side)
        
        // Button widths and spacing
        int buttonWidth = 12; // Width of each button including spacing
        int spacing = 2;      // Space between buttons
        
        // Calculate how many buttons can fit in the available width
        int maxButtons = Math.Max(1, availableWidth / (buttonWidth + spacing));
        
        // Get all visible buttons in order
        var buttons = ApprovalButtons();
        if (buttons.Count == 0) return;
        
        // If we have more buttons than can fit, we'll put the last button on a new line
        if (buttons.Count > maxButtons)
        {
            // Place first n-1 buttons on the first row
            int buttonsOnFirstRow = maxButtons - 1;
            for (int i = 0; i < buttonsOnFirstRow; i++)
            {
                buttons[i].X = 2 + i * (buttonWidth + spacing);
                buttons[i].Y = 2; // First row
            }
            
            // Place the last button on the second row (if there's room)
            if (buttonsOnFirstRow < buttons.Count)
            {
                buttons[buttons.Count - 1].X = 2;
                buttons[buttons.Count - 1].Y = 3; // Second row
            }
        }
        else
        {
            // All buttons fit on the first row
            for (int i = 0; i < buttons.Count; i++)
            {
                buttons[i].X = 2 + i * (buttonWidth + spacing);
                buttons[i].Y = 2; // First row
            }
        }
    }

    public override void OnLoaded()
    {
        base.OnLoaded();

        var config = TuiSessionContext.Config;
        statusBar.SetModel(config.Model.ActiveModelId);
        statusBar.SetSession(TuiSessionContext.LoopCtx?.SessionId ?? "");
        TuiSessionContext.StatusUpdate = SetStatus;

        AppendConvo("dotsy  ·  ready\n\n", Palette.Dim);
        AppendConvo("Type a message to start · /help for commands · Ctrl+C to cancel · Ctrl+Q to quit\n\n", Palette.Dim);
        foreach (var message in TuiSessionContext.StartupMessages)
            AppendConvo(message + "\n", message.StartsWith("[warn]", StringComparison.Ordinal) ? Palette.Warn : Palette.Dim);
        if (TuiSessionContext.StartupMessages.Count > 0)
            AppendConvo("\n", Palette.Normal);

        promptInput.SetFocus();

        Application.Invoke(() =>
        {
            promptInput.SetFocus();
            promptInput.PositionCursor();
        });

        if (Application.Navigation is { } nav)
            nav.FocusedChanged += (_, _) => Application.Invoke(HighlightFrameBorders);
    }

    private void HighlightFrameBorders()
    {
        var focused = Application.Navigation?.GetFocused();
        leftFrame.ColorScheme = IsDescendant(focused, leftFrame) ? Palette.FocusedPanelScheme() : Palette.Scheme();
        rightFrame.ColorScheme = IsDescendant(focused, rightFrame) ? Palette.FocusedPanelScheme() : Palette.Scheme();
        leftFrame.SetNeedsDraw();
        rightFrame.SetNeedsDraw();
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
            Application.RequestStop();
            return true;
        }

        if (key.KeyCode == KeyCode.Esc && inspectFrame.Visible)
        {
            HideInspect();
            return true;
        }

        if (key.KeyCode == KeyCode.Esc && approvalFrame.Visible)
        {
            FocusFirstApprovalButton();
            return true;
        }

        // The approval overlay is modal-ish: trap Tab/Shift+Tab so focus cycles its buttons and
        // never escapes to the (hidden) entry field or the panels underneath it.
        if (approvalFrame.Visible && (key == Key.Tab || key == Key.Tab.WithShift))
        {
            CycleApprovalFocus(back: key == Key.Tab.WithShift);
            return true;
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
        if (key.KeyCode == KeyCode.Esc && !approvalFrame.Visible && !inspectFrame.Visible)
        {
            Application.Invoke(() => promptInput.SetFocus());
            return true;
        }

        // Adjust split with Alt+Left/Right when focus is in either panel or the divider (but not the input or approval/inspect overlays)
        var foc = Application.Navigation?.GetFocused();
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
                    return true; // consume — view already handled it; don't let Toplevel move focus
            }
        }

        if (key.KeyCode == KeyCode.Tab && !approvalFrame.Visible && !inspectFrame.Visible)
        {
            if (Application.Navigation?.GetFocused() == promptInput && TryShowCompletionPopup())
                return true;

            var focused = Application.Navigation?.GetFocused();
            View next = focused == convo
                ? toolCallList
                : IsDescendant(focused, rightFrame) && !IsDescendant(focused, fileFrame)
                    ? (fileFrame.Visible ? (View)fileList : (View)promptInput)
                : IsDescendant(focused, fileFrame)
                    ? (View)promptInput
                : convo; // covers _input, null, unknown
            Application.Invoke(() => next.SetFocus());
            return true; // always consume — never let Toplevel base run Tab navigation
        }

        // Typing a printable character outside the input redirects focus there.
        // We insert the char directly because returning false after SetFocus does
        // not re-route the key to the newly focused view in TG v2's routing model.
        if (!approvalFrame.Visible && !inspectFrame.Visible && IsPrintableChar(key))
        {
            var focused = Application.Navigation?.GetFocused();
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
        // Letters A–Z carry ShiftMask in KeyCode; strip it before the range check.
        var kc = (uint)(key.KeyCode & ~KeyCode.ShiftMask);
        return kc >= 32 && kc <= 126; // ASCII printable range (space … ~)
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
                Application.Invoke(() =>
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
        Frame.Width > 0 ? Frame.Width : Application.Driver?.Cols ?? 0;

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

        int delta = key == Key.CursorDown.WithAlt ? 1 : -1;
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
        Application.Invoke(() =>
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
        ShowInspectFrame();
    }

    private void RefreshChangedFiles()
    {
        var cwd = TuiSessionContext.Cwd;
        Task.Run(() =>
        {
            var files = GitContext.GetChangedFiles(cwd);
            Application.Invoke(() =>
            {
                fileRows.Clear();
                foreach (var f in files)
                {
                    var ct = f.IsNew ? FileChangeType.Added
                               : f.IsDeleted ? FileChangeType.Deleted
                               : FileChangeType.Modified;

                    int added = 0, deleted = 0;

                    if (!f.IsNew && !f.IsDeleted)
                    {
                        try
                        {
                            var repoPath = LibGit2Sharp.Repository.Discover(cwd);
                            if (repoPath != null)
                            {
                                using var repo = new LibGit2Sharp.Repository(repoPath);
                                var head = repo.Head;
                                if (head.Tip != null)
                                {
                                    var diff = repo.Diff.Compare<LibGit2Sharp.Patch>(head.Tip.Tree, LibGit2Sharp.DiffTargets.WorkingDirectory);
                                    foreach (var change in diff)
                                    {
                                        if (change.Path.Equals(f.Path, StringComparison.OrdinalIgnoreCase))
                                        {
                                            added = change.LinesAdded;
                                            deleted = change.LinesDeleted;
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                        catch { }
                    }

                    fileRows.Add(new FileRow(f.Path, added, deleted, ct, []));
                }
                UpdateFilePanel();
            });
        });
    }

    private void OnFileRowRender(object? sender, ListViewRowEventArgs e)
    {
        if (e.Row >= fileRows.Count) return;
        var selected = fileList.HasFocus && fileList.SelectedItem == e.Row;
        e.RowAttribute = selected
            ? new TGAttribute(ColorName16.White, ColorName16.DarkGray)
            : Palette.Normal;
    }

    private void OnFileSelected(object? sender, ListViewItemEventArgs e)
    {
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
        inspectText.Load(lines);
        inspectFrame.Title = $" {row.Path}  [Ctrl+C copy · Ctrl+A all · Esc close] ";
    }
    #endregion

    #region Size and wrapping
    private void ResizeInput()
    {
        var text = promptInput.Text?.ToString() ?? "";
        // ContentsChanged also fires on cursor movement; skip resize when text is unchanged.
        var width = Math.Max(1, promptInput.Frame.Width > 0 ? promptInput.Frame.Width : Application.Driver?.Cols - 4 ?? 20);
        if (text == lastInputText && width == lastInputWidth) return;
        lastInputText = text;
        lastInputWidth = width;

        int rows = Application.Driver?.Rows ?? 24;

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
        approvalFrame.Y     = Pos.AnchorEnd(5 + h);
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
    public void SetStatus(string state) => Application.Invoke(() =>
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
        Application.Invoke(() =>
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
                _ => Application.Invoke(ToggleStreamCursor),
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
        Application.Invoke(() =>
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
        Application.Invoke(() =>
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
        Application.Invoke(() => { promptInput.Text = promptHistory[promptHistoryIdx.Value]; promptInput.MoveEnd(); });
    }

    private void OnHistoryNext(object? sender, EventArgs e)
    {
        if (!promptHistoryIdx.HasValue) return;
        if (promptHistoryIdx < promptHistory.Count - 1)
        {
            promptHistoryIdx++;
            Application.Invoke(() => { promptInput.Text = promptHistory[promptHistoryIdx.Value]; promptInput.MoveEnd(); });
        }
        else
        {
            promptHistoryIdx = null;
            var draft = promptDraft;
            Application.Invoke(() => { promptInput.Text = draft; promptInput.MoveEnd(); });
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
            Application.Invoke(() => AppendConvo($"\n[warn] trajectory export failed: {ex.Message}\n", Palette.Warn));
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
            if (rune.GetColumns() <= 0) continue;
            // Replace emoji-presentation runes (terminal draws 2 cols, TG draws 1 -> grid desync).
            cells.Add(new Cell(attr, false, Glyphs.Safe(rune)));
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
