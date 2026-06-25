using Dotsy.Cli.Tui.Colors;
using System.Drawing;
using System.Text;
using Terminal.Gui.Editor;

namespace Dotsy.Cli.Tui;

// Read-only scrollable TextView: accepts focus but blocks all editing keys.
internal sealed class ScrollableText : Editor
{
    private int topRow;
    private int leftColumn;
    private List<List<Cell>>? snapshot;
    private int[]? rowStartOffsets;

    /// <summary>
    /// When true the view behaves like a read-only text editor: arrow/Page/Home/End keys move a
    /// visible caret (and scroll to keep it in view), Shift+navigation extends a selection, Ctrl+C /
    /// Ctrl+Insert copy the selection and Ctrl+A selects all. When false the same navigation keys
    /// are remapped to plain viewport scrolling and Ctrl+C keeps its global "cancel the agent"
    /// meaning (text copying isn't the priority).
    /// </summary>
    public bool EnableSelectionCopy { get; set; }

    /// <summary>
    /// When true the bottom row of the viewport is reserved as a frame line: it draws a horizontal
    /// rule (─) normally, and the horizontal scroll bar when the content is wider than the view. Used
    /// by the inspection panel, whose <see cref="FrameView"/> has no bottom border of its own.
    /// </summary>
    public bool ShowBottomBorder { get; set; }

    public ScrollableText()
    {
        CanFocus = true;
        ReadOnly = true;
        WordWrap = false;
        SetScheme(Palette.ReadOnlyTextScheme());

        // The Editor base binds Tab/Shift+Tab to insert-tab commands which consume the key even
        // when read-only. Drop those bindings so Tab/Shift+Tab bubble to AgentWindow and switch
        // focus between panels instead of being swallowed here.
        KeyBindings.Remove(Key.Tab);
        KeyBindings.Remove(Key.Tab.WithShift);
    }

    protected override bool OnKeyDown(Key key)
    {
        // Page keys scroll a full page from the current viewport position. We move by viewport
        // offset (not the Editor's caret PageUp/PageDown command) because the caret command only
        // scrolls far enough to keep the caret on screen: from the top/bottom edge that first press
        // nudges a single line before later presses page fully. Offset-based paging is consistent.
        if (key == Key.PageDown) { ScrollByPage(down: true);  return true; }
        if (key == Key.PageUp)   { ScrollByPage(down: false); return true; }

        // Clipboard keys are handled before the scroll bindings (and before the key bubbles up to
        // the window, which otherwise treats Ctrl+C as "cancel"). Works read-only.
        if (EnableSelectionCopy)
        {
            if (key == Key.InsertChar.WithCtrl)            { InvokeCommand(Command.Copy); return true; }
            if (key == Key.C.WithCtrl && HasSelection) { InvokeCommand(Command.Copy); return true; }
            if (key == Key.A.WithCtrl)                { SelectAll(); SetNeedsDraw(); return true; }

            // Editor-style caret navigation: arrow/Page/Home/End keys (plus their Ctrl word and
            // Shift extend-selection variants) move the caret and scroll to keep it in view. All of
            // these work while ReadOnly. Alt+arrows are excluded so they still bubble up to the
            // window for panel resizing.
            //
            // The commands are invoked directly rather than via key bindings: matching the bound
            // Shift+arrow key through the binding table proves unreliable here, so Shift+selection
            // would otherwise do nothing. Direct invocation always runs the command.
            if (!key.IsAlt)
            {
                foreach (var (selKey, command) in ShiftSelectionCommands)
                {
                    if (key == selKey)
                    {
                        InvokeCommand(command);
                        SetNeedsDraw();
                        return true;
                    }
                }

                foreach (var (navKey, command) in PlainNavigationCommands)
                {
                    if (key == navKey)
                    {
                        if (HasSelection) ClearSelection(); // plain navigation collapses the selection
                        InvokeCommand(command);
                        SetNeedsDraw();
                        return true;
                    }
                }
            }
        }

        if (key.IsAlt) return false;
        if (key == Key.CursorLeft.WithCtrl)  { ScrollAndInvalidate(leftColumn - 10, false); return true; }
        if (key == Key.CursorRight.WithCtrl) { ScrollAndInvalidate(leftColumn + 10, false); return true; }

        int pageH = ScrollMath.PageStep(Viewport.Height);
        switch (key.KeyCode)
        {
            case KeyCode.CursorUp:    ScrollAndInvalidate(topRow - 1);                           return true;
            case KeyCode.CursorDown:  ScrollAndInvalidate(topRow + 1);                           return true;
            case KeyCode.PageUp:      ScrollAndInvalidate(topRow - pageH);                       return true;
            case KeyCode.PageDown:    ScrollAndInvalidate(topRow + pageH);                       return true;
            case KeyCode.Home:        ScrollAndInvalidate(0);                                    return true;
            case KeyCode.End:         ScrollAndInvalidate(ScrollMath.MaxOffset(GetContentLineCount(), Viewport.Height)); return true;
            case KeyCode.CursorLeft:  ScrollAndInvalidate(leftColumn - 1, false);                return true;
            case KeyCode.CursorRight: ScrollAndInvalidate(leftColumn + 1, false);                return true;
            default:                  return false;
        }
    }

    // Shift+navigation keys mapped to the TextView command that extends the selection.
    private static readonly (Key Key, Command Command)[] ShiftSelectionCommands =
    [
        (Key.CursorLeft.WithShift,           Command.LeftExtend),
        (Key.CursorRight.WithShift,          Command.RightExtend),
        (Key.CursorUp.WithShift,             Command.UpExtend),
        (Key.CursorDown.WithShift,           Command.DownExtend),
        (Key.Home.WithShift,                 Command.LeftStartExtend),
        (Key.End.WithShift,                  Command.RightEndExtend),
        (Key.PageUp.WithShift,               Command.PageUpExtend),
        (Key.PageDown.WithShift,             Command.PageDownExtend),
        (Key.CursorLeft.WithCtrl.WithShift,  Command.WordLeftExtend),
        (Key.CursorRight.WithCtrl.WithShift, Command.WordRightExtend),
        (Key.Home.WithCtrl.WithShift,        Command.StartExtend),
        (Key.End.WithCtrl.WithShift,         Command.EndExtend),
    ];

    // Plain navigation keys mapped to the TextView command that moves the caret.
    private static readonly (Key Key, Command Command)[] PlainNavigationCommands =
    [
        (Key.CursorLeft,          Command.Left),
        (Key.CursorRight,         Command.Right),
        (Key.CursorUp,            Command.Up),
        (Key.CursorDown,          Command.Down),
        (Key.Home,                Command.LeftStart),
        (Key.End,                 Command.RightEnd),
        (Key.PageUp,              Command.PageUp),
        (Key.PageDown,            Command.PageDown),
        (Key.CursorLeft.WithCtrl, Command.WordLeft),
        (Key.CursorRight.WithCtrl, Command.WordRight),
        (Key.Home.WithCtrl,       Command.Start),
        (Key.End.WithCtrl,        Command.End),
    ];

    protected override bool OnMouseEvent(Mouse ev)
    {
        if (IsContextMenuMouseEvent(ev))
        {
            ev.Handled = true;
            return true;
        }

        return base.OnMouseEvent(ev);
    }

    // Scrolls the viewport by one page. In copy mode the base Editor keeps the viewport pinned to
    // the caret, so we also move the caret into the new viewport (its line start) — that prevents
    // the Editor from snapping the scroll back and makes every page press move a full page.
    private void ScrollByPage(bool down)
    {
        if (HasSelection) ClearSelection();
        if (!CanScrollVertical()) return;

        int newTop = ScrollMath.PageTop(Viewport.Y, Viewport.Height, GetContentLineCount(), down);

        if (rowStartOffsets is { Length: > 0 } offs)
            CaretOffset = offs[Math.Min(newTop, offs.Length - 1)];

        topRow = newTop;
        Viewport = new Rectangle(leftColumn, newTop, Viewport.Width, Viewport.Height);
        SetNeedsDraw();
        SuperView?.SetNeedsDraw();
    }

    private void ScrollAndInvalidate(int topRow)
    {
        if (HasSelection) ClearSelection(); // plain navigation cancels any active selection
        this.topRow = CanScrollVertical()
            ? ScrollMath.ClampOffset(topRow, GetContentLineCount(), Viewport.Height)
            : 0;
        Viewport = new Rectangle(leftColumn, this.topRow, Viewport.Width, Viewport.Height);
        SetNeedsDraw();
        SuperView?.SetNeedsDraw();
    }

    private void ScrollAndInvalidate(int leftColumn, bool vertical)
    {
        this.leftColumn = CanScrollHorizontal()
            ? ScrollMath.ClampOffset(leftColumn, GetMaxLineWidth(), Viewport.Width)
            : 0;
        Viewport = new Rectangle(this.leftColumn, topRow, Viewport.Width, Viewport.Height);
        SetNeedsDraw();
        SuperView?.SetNeedsDraw();
    }

    /// <summary>Loads cell-list content, storing per-cell color attributes and pushing plain text to the Editor document.</summary>
    public void LoadText(List<List<Cell>> lines)
    {
        snapshot = lines;
        // Pre-compute the document offset at which each snapshot row starts.
        // Each row contributes row.Count chars + 1 newline (the last row has no trailing \n, but
        // the +1 makes selection-range checks safe and slightly over-inclusive, which is harmless).
        rowStartOffsets = new int[lines.Count];
        int off = 0;
        for (int i = 0; i < lines.Count; i++) { rowStartOffsets[i] = off; off += lines[i].Count + 1; }

        var sb = new StringBuilder();
        for (int i = 0; i < lines.Count; i++)
        {
            foreach (var cell in lines[i])
                sb.Append(cell.Grapheme);
            if (i < lines.Count - 1)
                sb.Append('\n');
        }
        Text = sb.ToString();
    }

    // Look up the cell color from the snapshot. snapshotRow/Col are absolute (not viewport-relative).
    private TGAttribute SnapshotAttributeAt(int snapshotRow, int snapshotCol)
    {
        if (snapshot is null || snapshotRow >= snapshot.Count) return Palette.Normal;
        var line = snapshot[snapshotRow];
        return snapshotCol < line.Count ? line[snapshotCol].Attribute ?? Palette.Normal : Palette.Normal;
    }

    // True when the cell falls inside the active selection (uses cached _rowStartOffsets).
    private bool IsCellSelected(int snapshotRow, int snapshotCol)
    {
        if (!HasSelection || rowStartOffsets is null || snapshotRow >= rowStartOffsets.Length)
            return false;
        int docOffset = rowStartOffsets[snapshotRow] + snapshotCol;
        int selLo = Math.Min(SelectionStart, SelectionEnd);
        int selHi = Math.Max(SelectionStart, SelectionEnd);
        return docOffset >= selLo && docOffset < selHi;
    }

    /// <summary>Scrolls the viewport to the last line of the document.</summary>
    public void MoveEnd()
    {
        topRow = ScrollMath.MaxOffset(GetContentLineCount(), Viewport.Height);
        leftColumn = 0;
        Viewport = new Rectangle(leftColumn, topRow, Viewport.Width, Viewport.Height);
        SetNeedsDraw();
    }

    /// <summary>Scrolls the viewport to the specified position.</summary>
    public void ScrollTo(Point p)
    {
        topRow = Math.Max(0, p.Y);
        leftColumn = Math.Max(0, p.X);
        Viewport = new Rectangle(leftColumn, topRow, Viewport.Width, Viewport.Height);
        SetNeedsDraw();
    }

    public bool CanScrollVertical() =>
        ScrollMath.CanScroll(GetContentLineCount(), Viewport.Height);

    private bool CanScrollHorizontal() =>
        ScrollMath.CanScroll(GetMaxLineWidth(), Viewport.Width);

    private int GetContentLineCount()
    {
        var text = Text?.ToString() ?? "";
        text = text.Replace("\r\n", "\n").TrimEnd('\n');
        return text.Length == 0 ? 0 : text.Count(ch => ch == '\n') + 1;
    }

    private int GetMaxLineWidth()
    {
        var text = Text?.ToString() ?? "";
        return text
            .Split('\n')
            .Select(line => CalculateDisplayWidth(line.TrimEnd('\r')))
            .DefaultIfEmpty(0)
            .Max();
    }

    private static int CalculateDisplayWidth(string text)
    {
        int width = 0;
        foreach (var rune in text.EnumerateRunes())
            width += Math.Max(1, Glyphs.GetColumns(rune));
        return width;
    }

    private bool IsContextMenuMouseEvent(Mouse ev) =>
        ev.Flags == ContextMenu?.MouseFlags
        || ev.Flags.HasFlag(MouseFlags.RightButtonPressed)
        || ev.Flags.HasFlag(MouseFlags.RightButtonReleased)
        || ev.Flags.HasFlag(MouseFlags.RightButtonClicked)
        || ev.Flags.HasFlag(MouseFlags.RightButtonDoubleClicked)
        || ev.Flags.HasFlag(MouseFlags.RightButtonTripleClicked);

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var handled = base.OnDrawingContent(context);

        int width = Viewport.Width;
        int height = Viewport.Height;
        int maxLineWidth = GetMaxLineWidth();
        bool vertical = GetContentLineCount() > height;
        bool horizontal = maxLineWidth > width;

        // Overdraw cell content with per-cell colors from the snapshot. The base Editor renders
        // everything in the scheme's Normal color; here we repaint each visible character with
        // the TGAttribute baked in at AppendConvo time (Bright, Err, Cmd, etc.).
        // Selected cells are left as-is so the selection highlight drawn by the base is preserved.
        if (snapshot is { Count: > 0 })
        {
            int drawWidth  = width  - (vertical   ? 1 : 0);
            int drawHeight = height - (horizontal || ShowBottomBorder ? 1 : 0);
            int vOff = Viewport.Y;
            int hOff = Viewport.X;

            for (int row = 0; row < drawHeight; row++)
            {
                int sRow = vOff + row;
                if (sRow >= snapshot.Count) break;
                var snapshotLine = snapshot[sRow];

                for (int col = 0; col < drawWidth; col++)
                {
                    int sCol = hOff + col;
                    if (sCol >= snapshotLine.Count) break;
                    if (IsCellSelected(sRow, sCol)) continue;

                    SetAttribute(snapshotLine[sCol].Attribute ?? Palette.Normal);
                    Move(col, row);
                    AddStr(snapshotLine[sCol].Grapheme);
                }
            }
        }

        // Thumb position from the live scroll offset. Viewport.Y/X always reflects the current scroll
        // position: in copy mode the base Editor scrolls to follow the caret and updates Viewport;
        // in non-copy mode we update Viewport ourselves via ScrollAndInvalidate.
        int vOffset = Viewport.Y;
        int hOffset = Viewport.X;

        if (vertical)
            ScrollBar.DrawVertical(this, width - 1, 0, height, GetContentLineCount(), height, vOffset);

        // When both bars show, shorten the horizontal one by a column so it doesn't collide with
        // the vertical bar's bottom cap in the corner.
        if (horizontal)
            ScrollBar.DrawHorizontal(this, 0, height - 1, width - (vertical ? 1 : 0), maxLineWidth, width, hOffset);
        else if (ShowBottomBorder)
            DrawBottomBorderLine(0, height - 1, width - (vertical ? 1 : 0));

        return handled;
    }

    // Draws a plain horizontal frame line (─) across the bottom row. Stands in for the FrameView's
    // missing bottom border; the horizontal scroll bar overdraws it when the content is wide enough.
    private void DrawBottomBorderLine(int x, int y, int length)
    {
        if (TuiSessionContext.App.Driver is null || y < 0 || length <= 0)
            return;

        TuiSessionContext.App.Driver.SetAttribute((SuperView ?? this).GetScheme().Normal);
        for (int i = 0; i < length; i++)
        {
            Move(x + i, y);
            TuiSessionContext.App.Driver.AddRune(new Rune('─'));
        }
    }
}
