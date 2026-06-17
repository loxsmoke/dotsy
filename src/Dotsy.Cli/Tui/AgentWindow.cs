using System.Collections.ObjectModel;
using System.Drawing;
using System.Globalization;
using Dotsy.Core.Config;
using Dotsy.Core.Loop;
using Dotsy.Core.Providers;
using Dotsy.Core.Session;
using Dotsy.Core.Tools;
using Dotsy.Core.Utils;
using Terminal.Gui;
using TGAttribute = Terminal.Gui.Attribute;

namespace Dotsy.Cli.Tui;

public class AgentWindow : Toplevel, IDisposable
{
    private const int DefaultLeftPanelWidthPercentage = 70;
    private const int MinPanelWidth = 20;
    private const int SplitResizeStepPercentage = 1;

    // ── Controls ──────────────────────────────────────────────────────────────
    private readonly StatusBar      _statusBar;
    private readonly FrameView      _leftFrame;
    private readonly View           _divider;
    private readonly FrameView      _rightFrame;
    private readonly ScrollableText _convo;
    private readonly FrameView      _fileFrame;
    private readonly FileListView   _fileList;
    private readonly ToolListView   _toolList;
    private readonly InspectionFrameView _inspectFrame;
    private readonly TextView       _inspectText;
    private readonly FrameView      _approvalFrame;
    private readonly Label          _approvalMsg;
    private FlatButton?             _btnProject;
    private readonly Label          _promptLabel;
    private readonly MultilineInput _input;
    private readonly FrameView      _completionFrame;
    private readonly ListView       _completionList;
    private int    _leftPanelWidthPercentage;
    private int    _inputHeight    = 1;
    private string _lastInputText  = "";
    private int    _lastInputWidth = -1;

    // ── Slash completion ─────────────────────────────────────────────────────
    private readonly ObservableCollection<CompletionItem> _completionItems = new();

    // ── Streaming cursor ─────────────────────────────────────────────────────
    private readonly object _streamCursorLock = new();
    private System.Threading.Timer? _streamCursorTimer;
    private bool _streamCursorActive;
    private bool _streamCursorVisible;

    // ── File change rows ──────────────────────────────────────────────────────
    private readonly ObservableCollection<FileRow> _fileRows = new();

    // ── Conversation (Cell-based for per-segment colour) ──────────────────────
    private readonly List<List<Cell>> _conversationLines = [[]];
    // Lines at these indices must not be word-wrapped (diff lines with background padding)
    private readonly HashSet<int> _noWrapLineIndices = new();
    private int _convoWrapWidth;

    // ── Tool rows ─────────────────────────────────────────────────────────────
    // Tool calls accumulate across prompts; each prompt's calls share a group id so the panel
    // can bracket them with a half-frame gutter. _toolCount is a monotonic row counter.
    private readonly ObservableCollection<ToolRow> _toolRows = new();
    private volatile int _toolCount;
    private int _toolGroupSeq;

    // ── Approval ──────────────────────────────────────────────────────────────
    private TaskCompletionSource<ApprovalChoice>? _approvalTcs;

    // ── Command history ───────────────────────────────────────────────────────
    private readonly List<string> _history    = new();
    private int    _historyIdx  = -1; // -1 = not navigating; index into _history otherwise
    private string _historyDraft = "";  // text saved when history navigation started

    // ── Scenario ──────────────────────────────────────────────────────────────
    private CancellationTokenSource? _scenarioCts;
    private CancellationTokenSource? _splitStatusCts;
    private string? _splitStatusRestoreState;

    // ── Spinner ───────────────────────────────────────────────────────────────
    private readonly ProgressSpinner _spinner;

    // ─────────────────────────────────────────────────────────────────────────
    public AgentWindow()
    {
        X = 0; Y = 0;
        Width  = Dim.Fill();
        Height = Dim.Fill();
        ColorScheme = Palette.Scheme();
        _leftPanelWidthPercentage = NormalizeLeftPanelWidthPercentage(
            TuiSessionContext.Config.Tui.LeftPanelWidthPercentage);
        TuiSessionContext.Config.Tui.LeftPanelWidthPercentage = _leftPanelWidthPercentage;

        // Status bar
        _statusBar = new StatusBar();

        // Left: conversation (Tab-focusable, scroll only)
        _leftFrame = new FrameView
        {
            X = 0, Y = 1, Width = Dim.Percent(_leftPanelWidthPercentage), Height = Dim.Fill(1),
            Title = " Conversation ", ColorScheme = Palette.Scheme()
        };
        _leftFrame.Border!.Thickness = new Thickness(0, 1, 0, 1); // no left or right bar
        _convo = new ScrollableText
        {
            X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(),
            EnableSelectionCopy = true
        };
        _fileFrame = new FrameView
        {
            X = 0, Y = Pos.AnchorEnd(3), Width = Dim.Fill(), Height = 3,
            Title = " changed files ", Visible = false, ColorScheme = Palette.Scheme()
        };
        _fileFrame.Border!.Thickness = new Thickness(0, 1, 0, 0);
        _fileList = new FileListView
        {
            X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(),
            CanFocus = false, ColorScheme = Palette.Scheme() // enabled only when frame is visible
        };
        _fileList.SetSource(_fileRows);
        _fileList.RowGetter        =  idx => idx < _fileRows.Count ? _fileRows[idx] : null;
        _fileList.OpenSelectedItem += OnFileSelected;
        _fileFrame.Add(_fileList);
        _leftFrame.Add(_convo, _fileFrame);

        _convo.FrameChanged += (_, _) =>
        {
            int w = _convo.Frame.Width;
            if (w > 0 && w != _convoWrapWidth)
            {
                _convoWrapWidth = w;
                Application.Invoke(ReloadConvo);
            }
        };

        // TextView (base of ScrollableText) consumes printable keys via InvokeCommands,
        // so they never reach AgentWindow.OnKeyDown. Intercept here and redirect to _input.
        _convo.KeyDown += (_, key) =>
        {
            if (_approvalFrame?.Visible == true || _inspectFrame?.Visible == true) return;
            if (!IsPrintableChar(key)) return;
            key.Handled = true;
            _input?.SetFocus();
            _input?.InsertText(key.AsRune.ToString());
        };

        // Explicit 1-column divider: draws ┬ at top, │ in middle, ┴ at bottom so the
        // T-junction characters are always correct regardless of LineCanvas corner merging.
        _divider = new PaneDivider
        {
            X = Pos.Right(_leftFrame), Y = 1, Width = 1, Height = Dim.Fill(1),
            CanFocus = false, ColorScheme = Palette.Scheme()
        };

        // Right: tool list (Tab-focusable, arrow-key selection)
        _rightFrame = new FrameView
        {
            X = Pos.Right(_divider), Y = 1, Width = Dim.Fill(), Height = Dim.Fill(1),
            Title = " Tools ", ColorScheme = Palette.Scheme()
        };
        _rightFrame.Border!.Thickness = new Thickness(0, 1, 0, 1); // no left or right bar
        _toolList = new ToolListView
        {
            X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(),
            CanFocus = true, ColorScheme = Palette.Scheme()
        };
        _toolList.SetSource(_toolRows);
        _toolList.RowGetter         = idx => idx >= 0 && idx < _toolRows.Count ? _toolRows[idx] : null;
        _toolList.RowRender        += OnToolRowRender;
        _toolList.OpenSelectedItem += OnToolSelected;
        _rightFrame.Add(_toolList);

        // Approval overlay (height=6: row 0=msg, row 2=main buttons, row 3=project button)
        _approvalFrame = new FrameView
        {
            X = 0, Y = Pos.AnchorEnd(6), Width = Dim.Fill(), Height = 6,
            Title = " Tool approval ", Visible = false, ColorScheme = Palette.Scheme()
        };
        _approvalMsg = new Label { X = 2, Y = 0, Width = Dim.Fill(2), Text = "", ColorScheme = Palette.Scheme() };

        var btnOnce   = new FlatButton("Allow once")        { X = 2,                        Y = 2 };
        var btnAlways = new FlatButton("Always allow")      { X = Pos.Right(btnOnce) + 2,   Y = 2 };
        var btnDeny   = new FlatButton("Deny")              { X = Pos.Right(btnAlways) + 2, Y = 2 };
        _btnProject   = new FlatButton("Allow for project") { X = 2,                        Y = 3, Visible = false };

        btnOnce.Fired    += (_, _) => CompleteApproval(ApprovalChoice.AllowOnce);
        btnAlways.Fired  += (_, _) => CompleteApproval(ApprovalChoice.AlwaysAllow);
        btnDeny.Fired    += (_, _) => CompleteApproval(ApprovalChoice.Deny);
        _btnProject.Fired += (_, _) => CompleteApproval(ApprovalChoice.AllowForProject);

        _approvalFrame.Add(_approvalMsg, btnOnce, btnAlways, btnDeny, _btnProject);

        // Inspect overlay — full-width, drawn on top of both panels
        _inspectFrame = new InspectionFrameView
        {
            X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(1),
            Title = " Inspection  [Ctrl+C copy · Ctrl+A all · Esc close] ", Visible = false, ColorScheme = Palette.Scheme()
        };
        _inspectText = new ScrollableText
        {
            X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(),
            ColorScheme = Palette.ReadOnlyTextScheme(),
            ShowScrollBars = false,
            EnableSelectionCopy = true
        };
        _inspectFrame.ContentView = _inspectText;
        _inspectFrame.Add(_inspectText);

        // Input bar — expands vertically as text wraps (up to 20 % of screen)
        _promptLabel = new Label
        {
            X = 0, Y = Pos.AnchorEnd(1), Width = 2, Height = 1, Text = "> ",
            ColorScheme = Palette.Scheme()
        };
        _input = new MultilineInput
        {
            X = 2, Y = Pos.AnchorEnd(1), Width = Dim.Fill(2), Height = 1,
        };
        _input.Submitted        += OnInputSubmitted;
        _input.ContentsChanged  += (_, _) =>
        {
            ResizeInput();
            UpdateCompletionPopup();
        };
        _input.HistoryPrev      += OnHistoryPrev;
        _input.HistoryNext      += OnHistoryNext;
        _input.QuitRequested    += (_, _) => Application.RequestStop();
        _input.CancelRequested  += (_, _) => _scenarioCts?.Cancel();

        _completionFrame = new FrameView
        {
            X = 2, Y = Pos.AnchorEnd(8), Width = 42, Height = 7,
            Title = " complete ", Visible = false, ColorScheme = Palette.Scheme()
        };
        _completionList = new ListView
        {
            X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(),
            CanFocus = true, ColorScheme = Palette.Scheme()
        };
        _completionList.SetSource(_completionItems);
        _completionList.OpenSelectedItem += (_, _) => ApplySelectedCompletion();
        _completionFrame.Add(_completionList);

        // _input first so it receives initial focus; inspect overlay last so it renders on top
        Add(_statusBar, _promptLabel, _input, _leftFrame, _divider, _rightFrame, _completionFrame, _approvalFrame, _inspectFrame);

        _spinner = new ProgressSpinner(frame => 
            Application.Invoke(() => _statusBar.SetSpinnerFrame(frame)));
    }

    public override void OnLoaded()
    {
        base.OnLoaded();

        var config = TuiSessionContext.Config;
        _statusBar.SetModel(config.Model.ActiveModelId);
        _statusBar.SetSession(TuiSessionContext.LoopCtx?.SessionId ?? "");
        TuiSessionContext.StatusUpdate = SetStatus;

        AppendConvo("dotsy  ·  ready\n\n", Palette.Dim);
        AppendConvo("Type a message to start · /help for commands · Ctrl+C to cancel · Ctrl+Q to quit\n\n", Palette.Dim);
        foreach (var message in TuiSessionContext.StartupMessages)
            AppendConvo(message + "\n", message.StartsWith("[warn]", StringComparison.Ordinal) ? Palette.Warn : Palette.Dim);
        if (TuiSessionContext.StartupMessages.Count > 0)
            AppendConvo("\n", Palette.Normal);

        _input.SetFocus();

        Application.Invoke(() =>
        {
            _input.SetFocus();
            _input.PositionCursor();
        });

        if (Application.Navigation is { } nav)
            nav.FocusedChanged += (_, _) => Application.Invoke(UpdateFrameBorders);
    }

    protected override bool OnKeyDown(Key key)
    {
        // Ctrl+C cancels the running agent regardless of which panel is focused.
        // Ctrl+Q quit is handled via _input.QuitRequested when the input has focus;
        // also handle it here so it works when focus is on the conversation/tool panels.
        // key.KeyCode for modifier combos includes the mask (Key.Q.WithCtrl not KeyCode.Q).
        if (key == new Key(KeyCode.C).WithCtrl)
        {
            _scenarioCts?.Cancel();
            return true;
        }

        if (key == new Key(KeyCode.Q).WithCtrl)
        {
            Application.RequestStop();
            return true;
        }

        if (key.KeyCode == KeyCode.Esc && _inspectFrame.Visible)
        {
            HideInspect();
            return true;
        }

        if (key.KeyCode == KeyCode.Esc && _approvalFrame.Visible)
        {
            FocusFirstApprovalButton();
            return true;
        }

        // The approval overlay is modal-ish: trap Tab/Shift+Tab so focus cycles its buttons and
        // never escapes to the (hidden) entry field or the panels underneath it.
        if (_approvalFrame.Visible && (key == Key.Tab || key == Key.Tab.WithShift))
        {
            CycleApprovalFocus(back: key == Key.Tab.WithShift);
            return true;
        }

        if (_completionFrame.Visible)
        {
            switch (key.KeyCode)
            {
                case KeyCode.Esc:
                    HideCompletionPopup();
                    _input.SetFocus();
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
        if (key.KeyCode == KeyCode.Esc && !_approvalFrame.Visible && !_inspectFrame.Visible)
        {
            Application.Invoke(() => _input.SetFocus());
            return true;
        }

        var foc = Application.Navigation?.GetFocused();
        if ((key == Key.CursorLeft.WithAlt || key == Key.CursorRight.WithAlt)
            && TryResizePanelSplit(key, foc))
            return true;

        // Prevent boundary navigation from stealing focus away from content panels
        if (foc == _convo || foc == _toolList || foc == _fileList)
        {
            switch (key.KeyCode)
            {
                case KeyCode.CursorUp:    case KeyCode.CursorDown:
                case KeyCode.PageUp:      case KeyCode.PageDown:
                case KeyCode.Home:        case KeyCode.End:
                case KeyCode.CursorLeft:  case KeyCode.CursorRight:
                    return true; // consume — view already handled it; don't let Toplevel move focus
            }
        }

        if (key.KeyCode == KeyCode.Tab && !_approvalFrame.Visible && !_inspectFrame.Visible)
        {
            if (Application.Navigation?.GetFocused() == _input && TryShowCompletionPopup())
                return true;

            var focused = Application.Navigation?.GetFocused();
            View next = focused == _convo
                ? (_fileFrame.Visible ? (View)_fileList : _toolList)
                : IsDescendant(focused, _fileFrame)  ? _toolList
                : IsDescendant(focused, _rightFrame) ? (View)_input
                : _convo; // covers _input, null, unknown
            Application.Invoke(() => next.SetFocus());
            return true; // always consume — never let Toplevel base run Tab navigation
        }

        // Typing a printable character outside the input redirects focus there.
        // We insert the char directly because returning false after SetFocus does
        // not re-route the key to the newly focused view in TG v2's routing model.
        if (!_approvalFrame.Visible && !_inspectFrame.Visible && IsPrintableChar(key))
        {
            var focused = Application.Navigation?.GetFocused();
            if (focused != _input)
            {
                _input.SetFocus();
                _input.InsertText(key.AsRune.ToString());
                return true;
            }
        }

        return base.OnKeyDown(key);
    }

    private bool TryResizePanelSplit(Key key, View? focused)
    {
        bool leftFocused = focused == _convo || IsDescendant(focused, _fileFrame);
        bool rightFocused = IsDescendant(focused, _rightFrame);
        if (!leftFocused && !rightFocused)
            return false;

        int delta = key == Key.CursorRight.WithAlt ? SplitResizeStepPercentage : -SplitResizeStepPercentage;
        SetLeftPanelWidthPercentage(_leftPanelWidthPercentage + delta, persist: true);
        return true;
    }

    private void SetLeftPanelWidthPercentage(int percentage, bool persist)
    {
        var clamped = ClampLeftPanelWidthPercentage(
            NormalizeLeftPanelWidthPercentage(percentage),
            GetLayoutWidth());
        if (clamped == _leftPanelWidthPercentage)
            return;

        _leftPanelWidthPercentage = clamped;
        TuiSessionContext.Config.Tui.LeftPanelWidthPercentage = clamped;
        _leftFrame.Width = Dim.Percent(clamped);
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
            _statusBar.SetState($"split not saved: {msg}");
    }

    private void ShowTemporarySplitStatus(string message)
    {
        _splitStatusRestoreState ??= _statusBar.State;
        _splitStatusCts?.Cancel();
        _splitStatusCts?.Dispose();
        var cts = new CancellationTokenSource();
        _splitStatusCts = cts;
        var restoreState = _splitStatusRestoreState;

        _statusBar.SetState(message);

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), cts.Token);
                Application.Invoke(() =>
                {
                    if (!cts.IsCancellationRequested && ReferenceEquals(_splitStatusCts, cts))
                    {
                        _splitStatusCts = null;
                        _splitStatusRestoreState = null;
                        _statusBar.SetState(restoreState ?? "idle");
                    }
                });
            }
            catch (OperationCanceledException) { }
        });
    }

    private int GetLayoutWidth() =>
        Frame.Width > 0 ? Frame.Width : Application.Driver?.Cols ?? 0;

    private static int NormalizeLeftPanelWidthPercentage(int percentage) =>
        percentage is > 0 and < 100
            ? percentage
            : DefaultLeftPanelWidthPercentage;

    private static int ClampLeftPanelWidthPercentage(int percentage, int totalWidth)
    {
        if (totalWidth <= 0 || totalWidth < MinPanelWidth * 2 + 1)
            return Math.Clamp(percentage, 1, 99);

        int minPct = (int)Math.Ceiling(MinPanelWidth * 100.0 / totalWidth);
        int maxPct = (int)Math.Floor((totalWidth - MinPanelWidth - 1) * 100.0 / totalWidth);
        return Math.Clamp(percentage, minPct, maxPct);
    }

    // ══ Frame border highlighting ═════════════════════════════════════════════

    private void UpdateFrameBorders()
    {
        var focused = Application.Navigation?.GetFocused();
        _leftFrame.ColorScheme  = IsDescendant(focused, _leftFrame)  ? Palette.FocusedPanelScheme() : Palette.Scheme();
        _rightFrame.ColorScheme = IsDescendant(focused, _rightFrame) ? Palette.FocusedPanelScheme() : Palette.Scheme();
        _leftFrame.SetNeedsDraw();
        _rightFrame.SetNeedsDraw();
    }

    private static bool IsPrintableChar(Key key)
    {
        if (key.IsCtrl || key.IsAlt) return false;
        // Letters A–Z carry ShiftMask in KeyCode; strip it before the range check.
        var kc = (uint)(key.KeyCode & ~KeyCode.ShiftMask);
        return kc >= 32 && kc <= 126; // ASCII printable range (space … ~)
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

    // ══ Public thread-safe conversation API ═══════════════════════════════════

    public void WriteConvo(string text) =>
        Application.Invoke(() => AppendConvo(text, Palette.Normal));

    public void WriteConvoError(string text) =>
        Application.Invoke(() => AppendConvo(text, Palette.Err));

    public void WriteConvoBullet(string text) =>
        Application.Invoke(() =>
        {
            AppendConvo("• ", Palette.Bullet);
            AppendConvo(text + "\n", Palette.Bright);
        });

    public void WriteConvoSubtask(string text) =>
        Application.Invoke(() =>
        {
            AppendConvo("  · ", Palette.Sub);
            AppendConvo(text + "\n", Palette.Normal);
        });

    // File change summary line: "  path  +N  -N"
    private static readonly TGAttribute FileAdd = new(ColorName16.Green, ColorName16.Black);
    private static readonly TGAttribute FileDel = new(ColorName16.Red,   ColorName16.Black);

    public void WriteConvoFileChange(string path, int added, int deleted) =>
        Application.Invoke(() =>
        {
            AppendConvo($"  {path}", Palette.Normal);
            AppendConvo($"  +{added}", FileAdd);
            AppendConvo($"  -{deleted}\n", FileDel);
        });

    public void AddFileDiff(string path, int added, int deleted, FileChangeType changeType, List<List<Cell>> diff) =>
        Application.Invoke(() =>
        {
            _fileRows.Add(new FileRow(path, added, deleted, changeType, diff));
            UpdateFilePanel();
        });

    private void UpdateFilePanel()
    {
        int total = _fileRows.Count;
        if (total == 0)
        {
            _fileList.CanFocus  = false;
            _fileFrame.Visible  = false;
            _convo.Height = Dim.Fill();
            _leftFrame.SetNeedsDraw();
            return;
        }

        int addedFiles   = _fileRows.Count(r => r.ChangeType == FileChangeType.Added);
        int deletedFiles = _fileRows.Count(r => r.ChangeType == FileChangeType.Deleted);
        _fileFrame.Title = $" changed files ({total}, +{addedFiles} -{deletedFiles}) ";

        // Cap at 30% of the left-frame inner height (subtract 2 for its own border)
        int innerH = Math.Max(10, _leftFrame.Frame.Height - 2);
        int maxH   = Math.Max(3, (int)(innerH * 0.30));
        int wantH  = total + 2; // +2 for frame border rows
        int h      = Math.Clamp(wantH, 3, maxH);

        _fileFrame.Height  = h;
        _fileFrame.Y       = Pos.AnchorEnd(h);
        _fileFrame.Visible = true;
        _fileList.CanFocus = true;
        _convo.Height      = Dim.Fill(h);
        _leftFrame.SetNeedsDraw();
    }

    private void ResizeInput()
    {
        var text = _input.Text?.ToString() ?? "";
        // ContentsChanged also fires on cursor movement; skip resize when text is unchanged.
        var width = Math.Max(1, _input.Frame.Width > 0 ? _input.Frame.Width : Application.Driver?.Cols - 4 ?? 20);
        if (text == _lastInputText && width == _lastInputWidth) return;
        _lastInputText = text;
        _lastInputWidth = width;

        int rows = Application.Driver?.Rows ?? 24;

        int lines = CountWrappedLines(text, width);
        int maxH  = Math.Max(1, (int)(rows * 0.20));
        int h     = Math.Clamp(lines, 1, maxH);

        if (h == _inputHeight) return;
        _inputHeight = h;

        _input.Height        = Dim.Absolute(h);
        _input.Y             = Pos.AnchorEnd(h);
        _promptLabel.Y       = Pos.AnchorEnd(h);
        _leftFrame.Height    = Dim.Fill(h);
        _rightFrame.Height   = Dim.Fill(h);
        _divider.Height      = Dim.Fill(h);
        _approvalFrame.Y     = Pos.AnchorEnd(5 + h);
        _completionFrame.Y   = Pos.AnchorEnd(h + Math.Max(3, _completionFrame.Frame.Height));
        _convo.MoveEnd();
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

    public void WriteConvoDiffHdr(string text) =>
        Application.Invoke(() => AppendConvo(text, Palette.DiffHdr));

    public void WriteConvoDiffAdd(int lineNum, string text) =>
        Application.Invoke(() => AddDiffLine(lineNum, '+', text,
            new TGAttribute(ColorName16.BrightGreen, ColorName16.Green),
            new TGAttribute(ColorName16.BrightGreen, ColorName16.Green)));

    public void WriteConvoDiffDel(int lineNum, string text) =>
        Application.Invoke(() => AddDiffLine(lineNum, '-', text,
            new TGAttribute(ColorName16.BrightRed, ColorName16.Red),
            new TGAttribute(ColorName16.BrightRed, ColorName16.Red)));

    public void WriteConvoDiffCtx(int lineNum, string text) =>
        Application.Invoke(() => AddDiffLine(lineNum, ' ', text,
            new TGAttribute(ColorName16.DarkGray, ColorName16.Black),
            Palette.DiffCtx));

    // Full-width diff line: indent + line-num + indicator + content + background padding
    private void AddDiffLine(int lineNum, char indicator, string content,
        TGAttribute numAttr, TGAttribute lineAttr)
    {
        const int PadWidth = 160;
        const int Indent   = 2;

        if (_conversationLines[^1].Count > 0) _conversationLines.Add([]);
        var line = _conversationLines[^1];

        Cell C(char ch, TGAttribute a) => new(a, false, new System.Text.Rune(ch));

        // Indent — coloured for add/del, normal for context
        var indentAttr = indicator == ' ' ? Palette.Normal : lineAttr;
        for (var i = 0; i < Indent; i++) line.Add(C(' ', indentAttr));

        // Line number: 4 chars right-aligned
        foreach (var ch in lineNum.ToString().PadLeft(4))
            line.Add(C(ch, numAttr));

        // Space + indicator + space
        line.Add(C(' ', lineAttr));
        line.Add(C(indicator, lineAttr));
        line.Add(C(' ', lineAttr));

        // Content (rune-aware; TextToCells strips CR/LF)
        line.AddRange(TextToCells(content, lineAttr));

        // Pad add/del lines to fill the terminal width (capped so they don't word-wrap)
        if (indicator != ' ')
        {
            int padTo = Math.Min(PadWidth, _convoWrapWidth > 0 ? _convoWrapWidth : PadWidth);
            while (line.Count < padTo)
                line.Add(C(' ', lineAttr));
        }

        // Mark this line as no-wrap so it displays correctly even if wider than view
        int diffLineIdx = _conversationLines.Count - 1;
        _noWrapLineIndices.Add(diffLineIdx);

        _conversationLines.Add([]);
        ReloadConvo();
    }

    // ══ Public thread-safe tool API ═══════════════════════════════════════════

    public int AddTool(string name, string arg, string cwd = "", int group = 0)
    {
        var idx = Interlocked.Increment(ref _toolCount) - 1;
        Application.Invoke(() =>
        {
            while (_toolRows.Count <= idx)
                _toolRows.Add(new ToolRow("", "", "PENDING", 0, DateTimeOffset.Now));
            _toolRows[idx] = new ToolRow(name, arg, "RUNNING", 0, DateTimeOffset.Now, cwd, Group: group);
            ScrollToolListToEnd();
        });
        return idx;
    }

    // Keep the most recent tool visible as the list grows across prompts, mirroring how the
    // conversation panel tails its newest output. Does not change the selection.
    private void ScrollToolListToEnd()
    {
        int vh = Math.Max(1, _toolList.Viewport.Height);
        _toolList.TopItem = Math.Max(0, _toolRows.Count - vh);
        _toolList.SetNeedsDraw();
    }

    public void UpdateTool(int idx, string status, int elapsedSec)
    {
        Application.Invoke(() =>
        {
            if (idx < _toolRows.Count)
                _toolRows[idx] = _toolRows[idx] with { Status = status, Elapsed = elapsedSec };
        });
    }

    public void UpdateToolArg(int idx, string arg)
    {
        Application.Invoke(() =>
        {
            if (idx < _toolRows.Count)
                _toolRows[idx] = _toolRows[idx] with { Arg = arg };
        });
    }

    public void SetToolOutput(int idx, List<List<Cell>> output)
    {
        Application.Invoke(() =>
        {
            if (idx < _toolRows.Count)
                _toolRows[idx] = _toolRows[idx] with { Output = output };
        });
    }

    public void SetStatus(string state) => Application.Invoke(() =>
    {
        _splitStatusCts?.Cancel();
        _splitStatusCts?.Dispose();
        _splitStatusCts = null;
        _splitStatusRestoreState = null;
        _statusBar.SetState(state);
    });

    public void StartSpinner(string state)
    {
        SetStatus(state);
        _spinner.Start();
    }

    public void StopSpinner(string state)
    {
        _spinner.Stop();
        SetStatus(state);
    }

    private static readonly HashSet<string> WriteToolNames =
        new(["Write", "Edit", "MultiEdit"], StringComparer.OrdinalIgnoreCase);

    public async Task<ApprovalChoice> ShowApproval(string toolName, string displayArg)
    {
        _approvalTcs = new TaskCompletionSource<ApprovalChoice>();
        Application.Invoke(() =>
        {
            _approvalMsg.Text = $"  {toolName}  {displayArg}";
            if (_btnProject is not null)
                _btnProject.Visible = WriteToolNames.Contains(toolName);
            _approvalFrame.Visible = true;
            FocusFirstApprovalButton();
        });
        return await _approvalTcs.Task;
    }

    // ══ Private helpers ═══════════════════════════════════════════════════════

    private void AppendConvoError(string message)
    {
        var lines = message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        AppendConvo("\n", Palette.Normal);
        foreach (var line in lines)
            AppendConvo($"  {line}\n", Palette.Err);
        AppendConvo("\n", Palette.Normal);
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

    private void AppendConvo(string text, TGAttribute attr)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value == '\n') { _conversationLines.Add([]); continue; }
            if (rune.Value == '\r' || rune.GetColumns() <= 0) continue; // drop CR + zero-width (avoids grid desync)
            _conversationLines[^1].Add(new Cell(attr, false, Glyphs.Safe(rune)));
        }
        ReloadConvo();
    }

    private void StartStreamCursor()
    {
        lock (_streamCursorLock)
        {
            _streamCursorActive = true;
            _streamCursorTimer?.Dispose();
            _streamCursorTimer = new System.Threading.Timer(
                _ => Application.Invoke(ToggleStreamCursor),
                null, 500, 500);
        }
        ShowStreamCursor();
    }

    private void StopStreamCursor()
    {
        lock (_streamCursorLock)
        {
            _streamCursorActive = false;
            _streamCursorTimer?.Dispose();
            _streamCursorTimer = null;
        }
        HideStreamCursor();
    }

    private void ToggleStreamCursor()
    {
        lock (_streamCursorLock)
        {
            if (!_streamCursorActive) return;
        }

        if (_streamCursorVisible)
            HideStreamCursor();
        else
            ShowStreamCursor();
    }

    private void ShowStreamCursor()
    {
        Application.Invoke(() =>
        {
            lock (_streamCursorLock)
            {
                if (!_streamCursorActive || _streamCursorVisible) return;
                _streamCursorVisible = true;
            }
            ReloadConvo();
        });
    }

    private void HideStreamCursor()
    {
        Application.Invoke(() =>
        {
            lock (_streamCursorLock)
            {
                if (!_streamCursorVisible) return;
                _streamCursorVisible = false;
            }
            ReloadConvo();
        });
    }

    private void ReloadConvo()
    {
        var lines = BuildConvoSnapshot();
        // Both resets guard against the same Terminal.Gui bug: TextView.Load calls
        // _historyText.Clear() *before* ResetPosition(), so OnContentsChanged() fires
        // with stale (post-click) cursor and selection against the freshly loaded model.
        //   IsSelecting=false  → prevents ProcessAutocomplete→SelectedText→GetRegion
        //                        crashing with out-of-bounds GetRange (line 4662).
        //   CursorPosition=(0,0) → prevents ProcessInheritsPreviousColorScheme
        //                          crashing with stale CurrentColumn (line 5777).
        _convo.IsSelecting = false;
        _convo.CursorPosition = new Point(0, 0);
        _convo.Load(lines);
        _convo.MoveEnd();
    }

    private List<List<Cell>> BuildConvoSnapshot()
    {
        var lineCount = _conversationLines.Count;
        while (lineCount > 1 && _conversationLines[lineCount - 1].Count == 0)
            lineCount--;

        int width = _convo.Frame.Width > 0 ? _convo.Frame.Width : Application.Driver?.Cols ?? 80;
        
        // Account for potential vertical scrollbar which consumes 1 column on the right
        if (_convo.ShowScrollBars && _convo.CanScrollVertical())
            width = Math.Max(1, width - 1);

        var snapshot = new List<List<Cell>>();
        for (var i = 0; i < lineCount; i++)
        {
            if (_noWrapLineIndices.Contains(i))
                snapshot.Add(new List<Cell>(_conversationLines[i]));
            else
                snapshot.AddRange(WrapCellLine(_conversationLines[i], width));
        }

        if (snapshot.Count == 0) snapshot.Add([]);
        
        lock (_streamCursorLock)
        {
            if (_streamCursorVisible)
                snapshot[^1].Add(new Cell(Palette.Bright, false, new System.Text.Rune('▌')));
        }

        return snapshot;
    }

    // Display columns a cell occupies, per Terminal.Gui's own renderer (which advances the cursor
    // by Rune.GetColumns()). Wrapping uses this so wrapped lines match what TG actually draws. Emoji
    // are replaced with 1-column glyphs at cell creation (Glyphs.Safe), so every cell here has a
    // width TG and the terminal agree on; clamp to >= 1 as a guard.
    private static int CellColumns(Cell cell) => Math.Max(1, cell.Rune.GetColumns());

    // Word-wraps a single logical line (list of colored cells) into display lines at `width`
    // DISPLAY COLUMNS (not cell count). Preserves per-cell attributes exactly — no color bleed.
    private static List<List<Cell>> WrapCellLine(List<Cell> cells, int width)
    {
        if (cells.Count == 0) return [[]];
        if (width < 1) width = 1;

        int totalCols = 0;
        foreach (var c in cells) totalCols += CellColumns(c);
        if (totalCols <= width) return [new List<Cell>(cells)];

        var result = new List<List<Cell>>();
        var current = new List<Cell>();
        int curCols = 0;

        static Cell Copy(Cell c) => new(c.Attribute, false, c.Rune);

        int i = 0;
        while (i < cells.Count)
        {
            bool isSpace = cells[i].Rune.Value == ' ';
            int tokenStart = i;
            while (i < cells.Count && (cells[i].Rune.Value == ' ') == isSpace) i++;

            int tokenCols = 0;
            for (int j = tokenStart; j < i; j++) tokenCols += CellColumns(cells[j]);

            if (isSpace)
            {
                // Spaces (1 column each) fill remaining room on a non-empty line; skip at line start.
                if (curCols > 0)
                    for (int j = tokenStart; j < i && curCols < width; j++) { current.Add(Copy(cells[j])); curCols++; }
            }
            else if (curCols > 0 && curCols + tokenCols <= width)
            {
                // Word fits on the current line.
                for (int j = tokenStart; j < i; j++) { current.Add(Copy(cells[j])); curCols += CellColumns(cells[j]); }
            }
            else
            {
                // Word doesn't fit: flush (stripping trailing spaces), then hard-wrap the word by columns.
                if (curCols > 0)
                {
                    while (current.Count > 0 && current[^1].Rune.Value == ' ') current.RemoveAt(current.Count - 1);
                    result.Add(current);
                    current = new List<Cell>();
                    curCols = 0;
                }
                for (int j = tokenStart; j < i; j++)
                {
                    int cw = CellColumns(cells[j]);
                    if (curCols > 0 && curCols + cw > width) { result.Add(current); current = new List<Cell>(); curCols = 0; }
                    current.Add(Copy(cells[j]));
                    curCols += cw;
                }
            }
        }

        while (current.Count > 0 && current[^1].Rune.Value == ' ')
            current.RemoveAt(current.Count - 1);
        result.Add(current);

        return result;
    }

    private void OnToolRowRender(object? sender, ListViewRowEventArgs e)
    {
        if (e.Row >= _toolRows.Count) return;
        var row = _toolRows[e.Row];
        var selected = _toolList.HasFocus && _toolList.SelectedItem == e.Row;
        e.RowAttribute = selected
            ? new TGAttribute(ColorName16.White, ColorName16.DarkGray)
            : row.Status switch
            {
                "OK"      => Palette.Success,
                "ERR"     => Palette.Err,
                "RUNNING" => Palette.Running,
                "SKIP"    => Palette.Dim,
                _         => Palette.Dim
            };
    }

    private void OnToolSelected(object? sender, ListViewItemEventArgs e)
    {
        if (e.Value is ToolRow row && row.Name.Length > 0)
            ShowInspect(row);
    }

    private void OnFileRowRender(object? sender, ListViewRowEventArgs e)
    {
        if (e.Row >= _fileRows.Count) return;
        var selected = _fileList.HasFocus && _fileList.SelectedItem == e.Row;
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
            FileChangeType.Added   => "new file",
            FileChangeType.Deleted => "deleted",
            _                      => "modified"
        };
        AddLine($"  {row.Path}  [{typeLabel}]", Palette.Bright);
        lines.Add([]);
        AddLine($"  +{row.Added} added  -{row.Deleted} deleted", Palette.Normal);
        lines.Add([]);
        lines.AddRange(row.Diff);
        _inspectText.Load(lines);
        _inspectFrame.Title = $" {row.Path}  [Ctrl+C copy · Ctrl+A all · Esc close] ";
        ShowInspectFrame();
    }

    private void ShowInspect(ToolRow row)
    {
        var lines = new List<List<Cell>>();

        void AddLine(string text, TGAttribute attr) => lines.Add(TextToCells(text, attr));

        AddLine($"  Command  {row.Name} {row.Arg}", Palette.Cmd);
        lines.Add([]);
        if (!string.IsNullOrEmpty(row.Cwd))
            AddLine($"  Folder   {row.Cwd}", Palette.Dim);
        lines.Add([]);
        var sAttr = row.Status switch { "OK" => Palette.Success, "ERR" => Palette.Err, _ => Palette.Warn };
        AddLine($"  Status   {row.Status}  ({row.Elapsed}s {row.StartedAt.ToString("o", CultureInfo.InvariantCulture)})", sAttr);
        lines.Add([]);
        AddLine("  Output", Palette.Dim);
        lines.Add([]);

        if (row.Output is { Count: > 0 } output)
            lines.AddRange(output);
        else
            AddLine("  (no output recorded)", Palette.Dim);

        _inspectText.Load(lines);
        ShowInspectFrame();
    }

    private void ShowInspectFrame()
    {
        HideCompletionPopup();
        _promptLabel.Visible = false;
        _input.Visible = false;
        _inspectText.ScrollTo(0);
        _inspectText.ScrollTo(0, false);
        _inspectFrame.Visible = true;
        _inspectText.SetFocus();
    }

    private void HideInspect()
    {
        _inspectFrame.Visible = false;
        _promptLabel.Visible = true;
        _input.Visible = true;
        _toolList.SetFocus();
    }

    private void CompleteApproval(ApprovalChoice choice)
    {
        Application.Invoke(() =>
        {
            _approvalFrame.Visible = false;
            _input.SetFocus();
        });
        _approvalTcs?.TrySetResult(choice);
    }

    // Visible buttons of the approval overlay, in display order.
    private List<FlatButton> ApprovalButtons() =>
        _approvalFrame.Subviews.OfType<FlatButton>().Where(b => b.Visible).ToList();

    private void FocusFirstApprovalButton()
    {
        var first = ApprovalButtons().FirstOrDefault();
        if (first is not null) first.SetFocus();
        else _approvalFrame.SetFocus();
    }

    private void CycleApprovalFocus(bool back)
    {
        var buttons = ApprovalButtons();
        if (buttons.Count == 0) { _approvalFrame.SetFocus(); return; }
        var focused = Application.Navigation?.GetFocused();
        int idx = buttons.FindIndex(b => b == focused);
        int next = idx < 0
            ? 0
            : ((idx + (back ? -1 : 1)) % buttons.Count + buttons.Count) % buttons.Count;
        buttons[next].SetFocus();
    }

    private void OnHistoryPrev(object? sender, EventArgs e)
    {
        if (_history.Count == 0) return;
        if (_historyIdx == -1)
        {
            _historyDraft = _input.Text?.ToString() ?? "";
            _historyIdx   = _history.Count - 1;
        }
        else if (_historyIdx > 0)
        {
            _historyIdx--;
        }
        Application.Invoke(() => { _input.Text = _history[_historyIdx]; _input.MoveEnd(); });
    }

    private void OnHistoryNext(object? sender, EventArgs e)
    {
        if (_historyIdx == -1) return;
        if (_historyIdx < _history.Count - 1)
        {
            _historyIdx++;
            Application.Invoke(() => { _input.Text = _history[_historyIdx]; _input.MoveEnd(); });
        }
        else
        {
            _historyIdx = -1;
            var draft = _historyDraft;
            Application.Invoke(() => { _input.Text = draft; _input.MoveEnd(); });
        }
    }

    private void OnInputSubmitted(object? sender, string raw)
    {
        var trimmed = raw.Trim();
        if (string.IsNullOrEmpty(trimmed)) return;
        HideCompletionPopup();

        if (!string.IsNullOrEmpty(trimmed) && (_history.Count == 0 || _history[^1] != trimmed))
            _history.Add(trimmed);
        _historyIdx   = -1;
        _historyDraft = "";

        _input.Text = "";
        ResizeInput();

        // Slash commands handled synchronously on the UI thread
        if (trimmed.StartsWith('/'))
        {
            HandleSlashCommand(trimmed);
            return;
        }

        SubmitUserPrompt(trimmed, trimmed);
    }

    private void SubmitUserPrompt(string displayText, string promptText)
    {
        if (_scenarioCts is not null)
        {
            AppendConvo("[Agent is busy — press Ctrl+C to cancel]\n\n", Palette.Warn);
            return;
        }

        var loop    = TuiSessionContext.Loop;
        var loopCtx = TuiSessionContext.LoopCtx;
        var cwd     = TuiSessionContext.Cwd;

        if (loop is null || loopCtx is null)
        {
            AppendConvo("[Agent not initialized]\n\n", Palette.Err);
            return;
        }

        AppendConvo($"User › {displayText}\n\n", Palette.Cmd);

        // Tool calls persist across prompts; tag this prompt's calls with a fresh group id so the
        // panel can bracket them together. The changed-files panel still resets each prompt.
        var toolGroup = ++_toolGroupSeq;
        _fileRows.Clear();
        _fileFrame.Visible = false;
        _convo.Height = Dim.Fill();
        _leftFrame.SetNeedsDraw();

        loopCtx.Messages.Add(new UserMessage([new TextBlock(promptText)]));
        TuiSessionContext.Session?.Append(new SessionRecord
        {
            Type = "user",
            Cwd = cwd,
            Message = new { content = promptText }
        });

        loop.PermissionPrompter = async (toolName, rawArgs, token) =>
        {
            var displayArg = FormatRunApproval(toolName, rawArgs, cwd);
            var choice = await ShowApproval(toolName, displayArg);
            switch (choice)
            {
                case ApprovalChoice.AlwaysAllow:
                    TuiSessionContext.Permissions?.AlwaysAllow(toolName, rawArgs);
                    break;
                case ApprovalChoice.AllowForProject:
                    TuiSessionContext.Permissions?.AllowWriteForProject();
                    break;
                case ApprovalChoice.AllowOnce:
                    TuiSessionContext.Permissions?.AllowForSession(toolName, rawArgs);
                    break;
            }
            return choice switch
            {
                ApprovalChoice.AllowOnce      => PermissionDecision.AllowOnce,
                ApprovalChoice.AllowForProject => PermissionDecision.AllowForProject,
                ApprovalChoice.AlwaysAllow    => PermissionDecision.AlwaysAllow,
                _                             => PermissionDecision.Deny
            };
        };

        _scenarioCts = new CancellationTokenSource();
        var ct = _scenarioCts.Token;
        var toolTimers = new Dictionary<int, long>();
        var toolArgs   = new Dictionary<int, (string Name, string ArgsJson)>();
        var toolRowIndex = new Dictionary<int, int>(); // per-turn loop index -> absolute row index

        Task.Run(async () =>
        {
            try
            {
                StartSpinner("thinking…");
                bool assistantWritten = false;
                bool thinkingWritten = false;
                MarkdownRenderer? mdRenderer = null;

                await foreach (var ev in loop.RunAsync(loopCtx, cwd, ct))
                {
                    switch (ev)
                    {
                        case TextChunk tc:
                            if (!assistantWritten)
                            {
                                assistantWritten = true;
                                mdRenderer = new MarkdownRenderer(_convoWrapWidth, (text, attr) =>
                                    Application.Invoke(() => AppendConvo(text, attr)));
                                Application.Invoke(() => AppendConvo("\nAgent › ", Palette.Bullet));
                                StartStreamCursor();
                            }
                            HideStreamCursor();
                            mdRenderer!.Write(tc.Text);
                            ShowStreamCursor();
                            break;

                        case ThinkingChunk thk:
                            if (!thinkingWritten)
                            {
                                thinkingWritten = true;
                                Application.Invoke(() => AppendConvo("\nthink › ", Palette.Dim));
                            }
                            Application.Invoke(() => AppendConvo(thk.Text, Palette.Dim));
                            break;

                        case ToolStarted ts:
                        {
                            StopStreamCursor();
                            if (assistantWritten)
                                mdRenderer?.Flush();
                            toolTimers[ts.Index] = Environment.TickCount64;
                            toolArgs[ts.Index]   = (ts.Name, ts.Arg);
                            var displayArg = FormatPanelArgument(ts.Name, ts.Arg, cwd);
                            // Rows accumulate across prompts, so the loop's per-turn index is
                            // remapped to the absolute row index AddTool assigns.
                            var rowIdx = AddTool(ts.Name, displayArg, cwd, toolGroup);
                            toolRowIndex[ts.Index] = rowIdx;
                            // For Write, pre-populate inspect with the content being written
                            if (ts.Name == "Write"
                                && ToolPanelFormatter.GetWriteContent(ts.Arg) is { } wc)
                                SetToolOutput(rowIdx, FormatToolOutput(wc));
                            Application.Invoke(() => _statusBar.SetState($"running  {ts.Name}"));
                            if (TuiSessionContext.Config.Tui.Verbose)
                                Application.Invoke(() =>
                                    AppendConvo($"\n▶ {ts.Name} · {displayArg}\n", Palette.Dim));
                            break;
                        }

                        case ToolFinished tf:
                        {
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
                                if (ta.Name is "Edit" or "MultiEdit")
                                    editCells = FormatEditInspectCells(ta.Name, ta.ArgsJson, tf.Result.Content, cwd);
                                else if (FormatPanelResult(ta.Name, ta.ArgsJson, tf.Result.Content, cwd) is { } enriched)
                                    UpdateToolArg(rowIdx, enriched);
                            }
                            // Write success: keep the content preview set during ToolStarted
                            if (tf.Name != "Write" || tf.Result.IsError)
                                SetToolOutput(rowIdx, editCells ?? FormatToolOutput(tf.Result.Content));
                            if (tf.Result.IsError)
                                Application.Invoke(() => AppendConvo(
                                    $"  [✗ {tf.Name}] {tf.Result.Content}\n", Palette.Err));
                            else if (tf.Name.Equals("Done", StringComparison.OrdinalIgnoreCase) &&
                                     !string.IsNullOrWhiteSpace(tf.Result.Content))
                            {
                                if (!assistantWritten)
                                    Application.Invoke(() => AppendConvo("\nAgent › ", Palette.Bullet));
                                else
                                    mdRenderer?.Flush();

                                Application.Invoke(() => AppendConvo(tf.Result.Content.TrimEnd() + "\n", Palette.Normal));
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
                                Application.Invoke(() =>
                                    AppendConvo(preview + suffix + "\n", Palette.Dim));
                            }
                            break;
                        }

                        case PermissionRequired pr:
                        {
                            StopStreamCursor();
                            var choice = await ShowApproval(pr.ToolName, pr.DisplayArgument);
                            if (choice == ApprovalChoice.AllowForProject)
                                TuiSessionContext.Permissions?.AllowWriteForProject();
                            pr.Decision.TrySetResult(choice switch
                            {
                                ApprovalChoice.AllowOnce      => PermissionDecision.AllowOnce,
                                ApprovalChoice.AllowForProject => PermissionDecision.AllowForProject,
                                ApprovalChoice.AlwaysAllow    => PermissionDecision.AlwaysAllow,
                                _                             => PermissionDecision.Deny
                            });
                            break;
                        }

                        case CompactionOccurred co:
                            StopStreamCursor();
                            Application.Invoke(() => AppendConvo(
                                $"\n─── compacted ({co.TokensBefore:N0}→{co.TokensAfter:N0} tokens) ───\n\n",
                                Palette.Dim));
                            break;

                        case TurnComplete tc2:
                            StopStreamCursor();
                            if (assistantWritten)
                            {
                                mdRenderer?.Flush();
                                mdRenderer = null;
                                Application.Invoke(() => AppendConvo("\n\n", Palette.Normal));
                                assistantWritten = false;
                            }
                            if (thinkingWritten)
                            {
                                Application.Invoke(() => AppendConvo("\n", Palette.Normal));
                                thinkingWritten = false;
                            }
                            UpdateStatusBarFromCtx();
                            if (tc2.AnyWriteTools) RefreshChangedFiles();
                            {
                                var firstUserText = loopCtx.Messages
                                    .OfType<UserMessage>()
                                    .SelectMany(m => m.Content.OfType<TextBlock>())
                                    .Select(tb => tb.Text)
                                    .FirstOrDefault() ?? "";
                                var sessionTitle = firstUserText.Length > 50
                                    ? firstUserText[..50] + "…"
                                    : firstUserText;
                                TuiSessionContext.Session?.UpdateIndex(sessionTitle, cwd, TuiSessionContext.Config.Model.ActiveModelId);
                            }
                            break;

                        case RetryScheduled rs:
                            StopStreamCursor();
                            Application.Invoke(() => _statusBar.SetState(
                                $"⏳ retrying in {rs.DelaySeconds}s · attempt {rs.AttemptNumber}/{rs.MaxAttempts}"));
                            break;

                        case ReflectionOccurred ro:
                            StopStreamCursor();
                            Application.Invoke(() =>
                                AppendConvo($"\n[reflection: {ro.Error}]\n\n", Palette.Warn));
                            break;

                        case LoopEnded le:
                        {
                            StopStreamCursor();
                            if (assistantWritten)
                            {
                                mdRenderer?.Flush();
                                mdRenderer = null;
                                Application.Invoke(() => AppendConvo("\n\n", Palette.Normal));
                            }
                            if (thinkingWritten)
                            {
                                Application.Invoke(() => AppendConvo("\n", Palette.Normal));
                                thinkingWritten = false;
                            }
                            if (le.Reason == EndReason.Error && !string.IsNullOrEmpty(le.Message))
                                Application.Invoke(() => AppendConvoError(le.Message));
                            TryExportTrajectory(loopCtx, le);
                            var msg = le.Reason switch
                            {
                                EndReason.TaskComplete      => "idle",
                                EndReason.NudgeLimitReached => "idle",
                                EndReason.TurnLimitReached  => "idle  [turn limit]",
                                EndReason.Cancelled         => "idle  [cancelled]",
                                EndReason.Error             => "idle  [error]",
                                EndReason.ContextTooSmall   => "idle  [context full]",
                                _                           => "idle"
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
                Application.Invoke(() => AppendConvo("\n[cancelled]\n\n", Palette.Warn));
                TryExportTrajectory(loopCtx, new LoopEnded(EndReason.Cancelled));
            }
            catch (Exception ex)
            {
                StopSpinner("idle  [error]");
                Application.Invoke(() => AppendConvoError(
                    ex.InnerException is { } inner
                        ? $"{ex.GetType().Name}: {ex.Message}\n{inner.GetType().Name}: {inner.Message}"
                        : $"{ex.GetType().Name}: {ex.Message}"));
            }
            finally
            {
                _scenarioCts = null;
                Application.Invoke(() => _input.SetFocus());
            }
        }, ct);
    }

    private static string FormatRunApproval(string toolName, string rawArgs, string cwd)
    {
        try
        {
            if (TuiSessionContext.Registry?.TryGetTool(toolName, out var tool) != true || tool is null)
                return rawArgs;

            using var doc = System.Text.Json.JsonDocument.Parse(rawArgs);
            return tool.FormatRunApproval(doc.RootElement, cwd);
        }
        catch
        {
            return rawArgs;
        }
    }

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

    private static string FormatPanelArgument(string toolName, string rawArgs, string cwd)
    {
        try
        {
            if (TuiSessionContext.Registry?.TryGetTool(toolName, out var tool) != true || tool is null)
                return rawArgs;

            using var doc = System.Text.Json.JsonDocument.Parse(rawArgs);
            return tool.FormatPanelArgument(doc.RootElement, cwd);
        }
        catch
        {
            return rawArgs;
        }
    }

    private static string? FormatPanelResult(string toolName, string rawArgs, string resultContent, string cwd)
    {
        try
        {
            if (TuiSessionContext.Registry?.TryGetTool(toolName, out var tool) != true || tool is null)
                return null;

            using var doc = System.Text.Json.JsonDocument.Parse(rawArgs);
            return tool.FormatPanelResult(doc.RootElement, resultContent, cwd);
        }
        catch
        {
            return null;
        }
    }

    private bool TryShowCompletionPopup()
    {
        UpdateCompletionPopup(force: true);
        if (_completionItems.Count == 0) return false;

        _completionFrame.Visible = true;
        _completionList.SelectedItem = 0;
        _completionList.SetFocus();
        return true;
    }

    private void UpdateCompletionPopup(bool force = false)
    {
        if (!force && !_completionFrame.Visible) return;

        var text = (_input.Text?.ToString() ?? "").TrimEnd('\r', '\n');
        var items = BuildCompletionItems(text);

        _completionItems.Clear();
        foreach (var item in items)
            _completionItems.Add(item);

        if (_completionItems.Count == 0)
        {
            HideCompletionPopup();
            return;
        }

        var height = Math.Clamp(_completionItems.Count + 2, 3, 9);
        var width = Math.Clamp(_completionItems.Max(i => i.Display.Length) + 4, 20, 60);
        _completionFrame.Height = height;
        _completionFrame.Width = width;
        _completionFrame.Y = Pos.AnchorEnd(_inputHeight + height);
        _completionFrame.Visible = true;

        if (_completionList.SelectedItem < 0 || _completionList.SelectedItem >= _completionItems.Count)
            _completionList.SelectedItem = 0;
        _completionList.EnsureSelectedItemVisible();
        _completionFrame.SetNeedsDraw();
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
        if (_completionItems.Count == 0) return;
        var next = Math.Clamp(_completionList.SelectedItem + delta, 0, _completionItems.Count - 1);
        _completionList.SelectedItem = next;
        _completionList.EnsureSelectedItemVisible();
        _completionList.SetNeedsDraw();
    }

    private void ApplySelectedCompletion()
    {
        if (!_completionFrame.Visible || _completionItems.Count == 0)
            return;

        var idx = Math.Clamp(_completionList.SelectedItem, 0, _completionItems.Count - 1);
        var item = _completionItems[idx];
        HideCompletionPopup();
        _input.SetTextAndMoveEnd(item.Replacement);
        ResizeInput();
        _input.SetFocus();
    }

    private void HideCompletionPopup()
    {
        _completionFrame.Visible = false;
        _completionItems.Clear();
        _completionFrame.SetNeedsDraw();
    }

    private void HandleSlashCommand(string raw)
    {
        var parts = raw.TrimStart('/').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var cmd  = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
        var rest = parts.Length > 1 ? string.Join(" ", parts[1..]) : "";

        switch (cmd)
        {
            case "exit":
            case "quit":
                Application.RequestStop();
                break;

            case "clear":
                _conversationLines.Clear();
                _conversationLines.Add([]);
                _noWrapLineIndices.Clear();
                lock (_streamCursorLock)
                    _streamCursorVisible = false;
                ReloadConvo();
                _toolRows.Clear();
                _toolCount = 0;
                _toolGroupSeq = 0;
                _fileRows.Clear();
                _fileFrame.Visible = false;
                _convo.Height = Dim.Fill();
                _leftFrame.SetNeedsDraw();
                break;

            case "help":
            {
                AppendConvo("Slash commands:\n", Palette.Bright);
                const int helpNameCol = 24; // "  " + 22-char syntax column
                var helpDescWidth = Math.Max(20, _convo.Frame.Width - helpNameCol - 1);
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
                break;
            }

            case "tools":
            {
                var registry = TuiSessionContext.Registry;
                if (registry is null)
                {
                    AppendConvoError("tool registry not initialized");
                    break;
                }
                var defs = registry.GetToolDefinitions();
                AppendConvo($"  {defs.Count} tools registered\n\n", Palette.Dim);
                const int nameCol = 22; // "  " + 20-char name
                var descWidth = Math.Max(20, _convo.Frame.Width - nameCol - 1);
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
                break;
            }

            case "verbose":
            {
                var cfg = TuiSessionContext.Config;
                cfg.Tui.Verbose = !cfg.Tui.Verbose;
                var state = cfg.Tui.Verbose ? "on" : "off";
                AppendConvo($"  verbose {state}  (tool calls and results will{(cfg.Tui.Verbose ? "" : " not")} appear inline)\n\n", Palette.Dim);
                break;
            }

            case "config":
            {
                var cfg = TuiSessionContext.Config;
                if (string.IsNullOrEmpty(rest))
                {
                    // The active provider's model section (e.g. [model.openai]) is highlighted green.
                    var activeProvider = cfg.Model.Provider.ToLowerInvariant() switch
                    {
                        "azure_openai" => "azure",
                        var p          => p,
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
                        break;
                    }
                    var key   = rest[..spaceIdx].Trim();
                    var value = rest[(spaceIdx + 1)..].Trim();

                    // A real config key is always "section.key"; a dotless first word almost always
                    // means the user typed a sentence after a stray "/config " left by autocomplete.
                    if (!key.Contains('.'))
                    {
                        AppendConvo($"  '{rest}' isn't a config command.\n", Palette.Warn);
                        AppendConvo("  To chat with the agent, send your message without the leading /config.\n", Palette.Dim);
                        AppendConvo("  To change a setting, use /config <section.key> <value> (try /config list).\n\n", Palette.Dim);
                        break;
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
                            if (_scenarioCts is not null)
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
                        Application.Invoke(() => _statusBar.SetModel(TuiSessionContext.Config.Model.ActiveModelId));
                        AppendConvo("\n", Palette.Normal);
                    }
                    else
                        AppendConvoError($"config error: {msg}");
                }
                break;
            }

            case "model":
                if (string.IsNullOrEmpty(rest))
                {
                    var cfg       = TuiSessionContext.Config;
                    var provider  = ConfigLoader.GetProviderDisplayName(cfg.Model.Provider);
                    var keySource = ConfigLoader.GetApiKeySource(cfg);
                    var activeId  = cfg.Model.ActiveModelId;
                    AppendConvo("  Provider:  ", Palette.Dim); AppendConvo($"{provider}\n",     Palette.Normal);
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
                            if (failed)
                            {
                                AppendConvo("  Context:   ", Palette.Dim); AppendConvo("error fetching\n", Palette.Err);
                            }
                            else if (info != null)
                            {
                                AppendConvo("  Context:   ", Palette.Dim); AppendConvo($"{info.ContextWindow}\n", Palette.Bright);
                            }
                            else
                            {
                                AppendConvo("  Context:   ", Palette.Dim); AppendConvo("unknown\n", Palette.Warn);
                            }

                            AppendConvo("  Api key:   ", Palette.Dim);
                            if (keySource == "no key required")
                                AppendConvo("no key required\n\n", Palette.Bright);
                            else if (keySource == "not specified")
                                AppendConvo("not specified\n\n", Palette.Bright);
                            else
                                AppendConvo($"specified via {keySource}\n\n", Palette.Bright);
                        });
                    });
                }
                else
                {
                    TuiSessionContext.Config.Model.ActiveModelId = rest;
                    Application.Invoke(() => _statusBar.SetModel(rest));
                    AppendConvo($"model → {rest}\n\n", Palette.Success);
                }
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
            {
                var disc   = new SkillDiscovery(TuiSessionContext.Config.Skills, TuiSessionContext.Cwd);
                if (!string.IsNullOrWhiteSpace(rest))
                {
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
                else
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
                }
                break;
            }

            case "add":
            {
                if (string.IsNullOrEmpty(rest))
                {
                    AppendConvo("usage: /add <path>\n\n", Palette.Warn);
                }
                else
                {
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
                break;
            }

            case "compact":
                StartManualCompaction();
                break;

            case "undo":
            {
                if (_scenarioCts is not null)
                {
                    AppendConvo("cancel the current turn before undoing\n\n", Palette.Warn);
                    break;
                }

                var ctx = TuiSessionContext.LoopCtx;
                if (ctx is null)
                {
                    AppendConvoError("session context not initialized");
                    break;
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
                break;
            }

            default:
                AppendConvo($"unknown command: /{cmd}  (try /help)\n\n", Palette.Warn);
                break;
        }
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

    private void StartSelfCommand(string rawCommand, string question)
    {
        if (_scenarioCts is not null)
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

        _statusBar.SetState("building self context");
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
                    _statusBar.SetState("idle");
                    SubmitUserPrompt(rawCommand, prompt);
                });
            }
            catch (Exception ex)
            {
                Application.Invoke(() =>
                {
                    _statusBar.SetState("idle");
                    AppendConvoError($"self context failed: {ex.Message}");
                });
            }
        });
    }

    private void StartManualCompaction()
    {
        if (_scenarioCts is not null)
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

        _scenarioCts = new CancellationTokenSource();
        var ct = _scenarioCts.Token;

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
                _scenarioCts?.Dispose();
                _scenarioCts = null;
            }
        });
    }

    private void ResumeSession(string requestedId)
    {
        if (_scenarioCts is not null)
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

        _toolRows.Clear();
        _toolCount = 0;
        _toolGroupSeq = 0;
        _fileRows.Clear();
        _fileFrame.Visible = false;
        _convo.Height = Dim.Fill();
        _statusBar.SetSession(loaded.SessionId);
        UpdateStatusBarFromCtx();

        AppendConvo($"resumed session: {loaded.SessionId}\n", Palette.Success);
        AppendConvo($"messages loaded: {loaded.Messages.Count}\n\n", Palette.Dim);
    }

    private void UpdateStatusBarFromCtx()
    {
        var ctx    = TuiSessionContext.LoopCtx;
        var config = TuiSessionContext.Config;
        if (ctx is null) return;
        Application.Invoke(() =>
        {
            _statusBar.SetModel(config.Model.ActiveModelId);
            _statusBar.SetCtxPct(ctx.TokenBudget.UsagePct);
        });
    }

    private void RefreshChangedFiles()
    {
        var cwd = TuiSessionContext.Cwd;
        Task.Run(() =>
        {
            var files = GitContext.GetChangedFiles(cwd);
            Application.Invoke(() =>
            {
                _fileRows.Clear();
                foreach (var f in files)
                {
                    var ct = f.IsNew ? FileChangeType.Added
                           : f.IsDeleted ? FileChangeType.Deleted
                           : FileChangeType.Modified;
                    
                    int added = 0, deleted = 0;
                    
                    // Calculate line changes for modified files using LibGit2Sharp
                    if (!f.IsNew && !f.IsDeleted)
                    {
                        try
                        {
                            var repoPath = LibGit2Sharp.Repository.Discover(cwd);
                            if (repoPath != null)
                            {
                                using var repo = new LibGit2Sharp.Repository(repoPath);
                                // Get the current commit
                                var head = repo.Head;
                                if (head.Tip != null)
                                {
                                    // Compare working directory with HEAD (Patch carries per-file line counts)
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
                        catch
                        {
                            // If we can't calculate diff, fall back to 0, 0
                        }
                    }
                    
                    _fileRows.Add(new FileRow(f.Path, added, deleted, ct, []));
                }
                UpdateFilePanel();
            });
        });
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

    private static List<List<Cell>> FormatToolOutput(string content)
    {
        var lines = new List<List<Cell>>();
        foreach (var line in content.Split('\n'))
            lines.Add(TextToCells(line, Palette.Normal));
        return lines;
    }

    // Builds a colored inspect view for Edit / MultiEdit showing output then input parameters.
    private static List<List<Cell>> FormatEditInspectCells(string toolName, string argsJson, string resultContent, string cwd)
    {
        var lines = new List<List<Cell>>();

        List<Cell> Row(string text, TGAttribute attr) => TextToCells(text, attr);

        void Blank() => lines.Add([]);

        void Header(string title) => lines.Add(Row($"  {title}", Palette.Bright));

        void LabelValue(string label, string value, TGAttribute valueAttr)
        {
            var line = TextToCells($"  {label,-10}", Palette.Dim);
            line.AddRange(TextToCells(value, valueAttr));
            lines.Add(line);
        }

        void TextBlock(string sectionTitle, string text, TGAttribute lineAttr)
        {
            Header(sectionTitle);
            foreach (var rawLine in text.Split('\n'))
                lines.Add(Row("  " + rawLine.TrimEnd('\r'), lineAttr));
        }

        void EditSection(System.Text.Json.JsonElement edit)
        {
            if (edit.TryGetProperty("start_line", out var sl) && edit.TryGetProperty("end_line", out var el))
            {
                LabelValue("Lines", $"{sl.GetInt32()} – {el.GetInt32()}", Palette.Normal);
            }
            else
            {
                var old = edit.GetStringPropertyOrEmpty("old_string");
                Blank();
                TextBlock("Search:", old, Palette.Warn);
            }
            var @new = edit.GetStringPropertyOrEmpty("new_string");
            Blank();
            TextBlock("Replace:", @new, Palette.Success);
        }

        if (!string.IsNullOrWhiteSpace(resultContent))
        {
            Header("Output");
            foreach (var rawLine in resultContent.TrimEnd().Split('\n'))
                lines.Add(Row("  " + rawLine.TrimEnd('\r'), Palette.Normal));
            Blank();
        }

        System.Text.Json.JsonElement input;
        try { input = System.Text.Json.JsonDocument.Parse(argsJson).RootElement; }
        catch { return lines; }

        var path = input.GetStringPropertyOrEmpty("path");
        LabelValue("Path", MakeRelCwd(path, cwd), Palette.Normal);

        if (toolName == "Edit")
        {
            EditSection(input);
        }
        else if (toolName == "MultiEdit" &&
                 input.TryGetProperty("edits", out var editsEl) &&
                 editsEl.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            var idx = 1;
            foreach (var edit in editsEl.EnumerateArray())
            {
                Blank();
                Header($"Edit {idx}");
                EditSection(edit);
                idx++;
            }
        }

        return lines;
    }

    private static string MakeRelCwd(string path, string cwd)
    {
        if (string.IsNullOrEmpty(path)) return ".";
        try
        {
            var abs = Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(cwd, path));
            var cwdFull = Path.GetFullPath(cwd);
            if (abs.StartsWith(cwdFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return abs[(cwdFull.Length + 1)..];
            if (abs.Equals(cwdFull, StringComparison.OrdinalIgnoreCase))
                return ".";
        }
        catch { }
        return path;
    }

    // Re-applies the active theme to the whole view tree without a restart. Color attributes used
    // when drawing (Palette.*) resolve live, but ColorSchemes are captured per view, so reassign
    // them here. When a recolor map is supplied, already-rendered cells (conversation scrollback,
    // tool output, file diffs) are remapped from the previous theme's attributes to the new ones.
    private void ReapplyTheme(Func<TGAttribute, TGAttribute>? recolor = null)
    {
        if (recolor is not null)
        {
            RecolorCellLines(_conversationLines, recolor);
            foreach (var row in _toolRows)
                if (row.Output is { } output) RecolorCellLines(output, recolor);
            foreach (var row in _fileRows)
                RecolorCellLines(row.Diff, recolor);
        }

        ColorScheme = Palette.Scheme();
        Colors.ColorSchemes["Base"]   = Palette.Scheme();
        Colors.ColorSchemes["Menu"]   = Palette.Scheme();
        Colors.ColorSchemes["Dialog"] = Palette.Scheme();
        Colors.ColorSchemes["Error"]  = Palette.Scheme();
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
                case StatusBar sb:        sb.ApplyTheme();                            break;
                case FlatButton btn:      btn.ColorScheme = Palette.BtnScheme();       break;
                case MultilineInput mi:   mi.ColorScheme = Palette.InputScheme();      break;
                case ScrollableText st:   st.ColorScheme = Palette.ReadOnlyTextScheme(); break;
                default:                  sv.ColorScheme = Palette.Scheme();           break;
            }
            RethemeRecursive(sv);
        }
    }

    public new void Dispose()
    {
        _spinner?.Dispose();
        _streamCursorTimer?.Dispose();
        _splitStatusCts?.Cancel();
        _splitStatusCts?.Dispose();
        _scenarioCts?.Cancel();
        _scenarioCts?.Dispose();
        base.Dispose();
    }
}

internal sealed record CompletionItem(string Display, string Replacement)
{
    public override string ToString() => "  " + Display;
}

internal sealed record ToolRow(
    string Name,
    string Arg,
    string Status,
    int Elapsed,
    DateTimeOffset StartedAt,
    string Cwd = "",
    List<List<Cell>>? Output = null,
    int Group = 0)
{
    public override string ToString()
    {
        var icon = Status switch { "OK" => "✓", "ERR" => "✗", "RUNNING" => "◌", _ => " " };
        var tail = Status == "RUNNING" ? $" {Elapsed}s…" : $" {Elapsed}s";
        // Pad the name to a column, but always keep at least one space before the argument so
        // long tool names (e.g. "FindDefinitions") don't run straight into the argument text.
        var name = Name.Length >= 10 ? Name + " " : $"{Name,-10}";
        return $" {icon}  {name}{Arg,-28}{tail}";
    }
}

public enum FileChangeType { Modified, Added, Deleted }

internal sealed class InspectionFrameView : FrameView
{
    public TextView? ContentView { get; set; }

    protected override void OnDrawComplete(DrawContext? context)
    {
        base.OnDrawComplete(context);
        DrawScrollBars();
    }

    private void DrawScrollBars()
    {
        if (Application.Driver is null || ContentView is null)
            return;

        var frame = FrameToScreen();
        if (frame.Width <= 0 || frame.Height <= 0)
            return;

        int viewportWidth = Math.Max(1, ContentView.Viewport.Width);
        int viewportHeight = Math.Max(1, ContentView.Viewport.Height);
        var text = ContentView.Text?.ToString() ?? "";
        int contentLines = GetContentLineCount(text);
        int maxLineWidth = GetMaxLineWidth(text);
        bool showVertical = contentLines > viewportHeight;
        bool showHorizontal = maxLineWidth > viewportWidth;

        Application.Driver.SetAttribute(GetNormalColor());

        if (showVertical)
        {
            int height = frame.Height - 2;
            DrawVertical(frame.X + frame.Width - 1, frame.Y + 1, height, contentLines, viewportHeight, ContentView.TopRow);
        }

        if (showHorizontal)
        {
            int width = frame.Width - (showVertical ? 1 : 0);
            DrawHorizontal(frame.X, frame.Y + frame.Height - 1, width, maxLineWidth, viewportWidth, ContentView.LeftColumn);
        }
    }

    private static int GetMaxLineWidth(string text) =>
        text.Split('\n').Select(line => line.TrimEnd('\r').Length).DefaultIfEmpty(0).Max();

    private static int GetContentLineCount(string text)
    {
        text = text.Replace("\r\n", "\n").TrimEnd('\n');
        return text.Length == 0 ? 0 : text.Count(ch => ch == '\n') + 1;
    }

    private static void DrawVertical(int x, int y, int height, int totalRows, int viewportRows, int topRow)
    {
        if (height <= 0 || Application.Driver is null) return;
        if (height == 1)
        {
            Application.Driver.Move(x, y);
            Application.Driver.AddRune(new System.Text.Rune('░'));
            return;
        }

        Application.Driver.Move(x, y);
        Application.Driver.AddRune(new System.Text.Rune('▲'));
        if (height == 2)
        {
            Application.Driver.Move(x, y + 1);
            Application.Driver.AddRune(new System.Text.Rune('▼'));
            return;
        }

        int trackHeight = height - 2;
        int thumbHeight = Math.Max(1, trackHeight * viewportRows / Math.Max(1, totalRows));
        int maxTop = Math.Max(1, totalRows - viewportRows);
        int thumbTop = 1 + Math.Min(trackHeight - thumbHeight, topRow * (trackHeight - thumbHeight) / maxTop);

        for (int row = 1; row < height - 1; row++)
        {
            Application.Driver.Move(x, y + row);
            var ch = row >= thumbTop && row < thumbTop + thumbHeight ? '█' : '░';
            Application.Driver.AddRune(new System.Text.Rune(ch));
        }

        Application.Driver.Move(x, y + height - 1);
        Application.Driver.AddRune(new System.Text.Rune('▼'));
    }

    private static void DrawHorizontal(int x, int y, int width, int totalColumns, int viewportColumns, int leftColumn)
    {
        if (width <= 0 || Application.Driver is null) return;
        if (width == 1)
        {
            Application.Driver.Move(x, y);
            Application.Driver.AddRune(new System.Text.Rune('░'));
            return;
        }

        Application.Driver.Move(x, y);
        Application.Driver.AddRune(new System.Text.Rune('◄'));
        if (width == 2)
        {
            Application.Driver.Move(x + 1, y);
            Application.Driver.AddRune(new System.Text.Rune('►'));
            return;
        }

        int trackWidth = width - 2;
        int thumbWidth = Math.Max(1, trackWidth * viewportColumns / Math.Max(1, totalColumns));
        int maxLeft = Math.Max(1, totalColumns - viewportColumns);
        int thumbLeft = 1 + Math.Min(trackWidth - thumbWidth, leftColumn * (trackWidth - thumbWidth) / maxLeft);

        for (int col = 1; col < width - 1; col++)
        {
            Application.Driver.Move(x + col, y);
            var ch = col >= thumbLeft && col < thumbLeft + thumbWidth ? '█' : '░';
            Application.Driver.AddRune(new System.Text.Rune(ch));
        }

        Application.Driver.Move(x + width - 1, y);
        Application.Driver.AddRune(new System.Text.Rune('►'));
    }
}

/// <summary>One-column view that draws ┬ / │ / ┴ to form T-junctions where the two panel borders meet.</summary>
internal sealed class PaneDivider : View
{
    protected override bool OnDrawingContent()
    {
        if (Application.Driver is null) return base.OnDrawingContent();
        Application.Driver.SetAttribute(GetNormalColor());
        int h = Frame.Height;
        for (int y = 0; y < h; y++)
        {
            Move(0, y);
            Application.Driver.AddRune(new System.Text.Rune(y == 0 ? '┬' : y == h - 1 ? '┴' : '│'));
        }
        return true;
    }
}

internal sealed record FileRow(string Path, int Added, int Deleted, FileChangeType ChangeType, List<List<Cell>> Diff)
{
    public override string ToString() => ChangeType switch
    {
        FileChangeType.Added   => $"  + {Path}",
        FileChangeType.Deleted => $"  - {Path}",
        _                      => $"  ↳ {Path}   +{Added}  -{Deleted}"
    };
}
