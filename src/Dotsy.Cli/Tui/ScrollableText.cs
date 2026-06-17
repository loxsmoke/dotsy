using Terminal.Gui;

namespace Dotsy.Cli.Tui;

// Read-only scrollable TextView: accepts focus but blocks all editing keys.
internal sealed class ScrollableText : TextView
{
    public bool ShowScrollBars { get; set; } = true;

    /// <summary>
    /// When true the view behaves like a read-only text editor: arrow/Page/Home/End keys move a
    /// visible caret (and scroll to keep it in view), Shift+navigation extends a selection, Ctrl+C /
    /// Ctrl+Insert copy the selection and Ctrl+A selects all. When false the same navigation keys
    /// are remapped to plain viewport scrolling and Ctrl+C keeps its global "cancel the agent"
    /// meaning (text copying isn't the priority).
    /// </summary>
    public bool EnableSelectionCopy { get; set; }

    public ScrollableText()
    {
        CanFocus = true;
        ReadOnly = true;
        WordWrap = false;
        ColorScheme = Palette.ReadOnlyTextScheme();
    }

    protected override bool OnKeyDown(Key key)
    {
        // Clipboard keys are handled before the scroll bindings (and before the key bubbles up to
        // the window, which otherwise treats Ctrl+C as "cancel"). Works read-only.
        if (EnableSelectionCopy)
        {
            if (key == Key.InsertChar.WithCtrl)       { Copy(); return true; }
            if (key == Key.C.WithCtrl && IsSelecting) { Copy(); return true; }
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
                        if (IsSelecting) IsSelecting = false; // plain navigation collapses the selection
                        InvokeCommand(command);
                        SetNeedsDraw();
                        return true;
                    }
                }
            }
        }

        if (key.IsAlt) return false;
        if (key == Key.CursorLeft.WithCtrl)  { ScrollAndInvalidate(LeftColumn - 10, false); return true; }
        if (key == Key.CursorRight.WithCtrl) { ScrollAndInvalidate(LeftColumn + 10, false); return true; }

        int pageH = Math.Max(1, Viewport.Height - 1);
        switch (key.KeyCode)
        {
            case KeyCode.CursorUp:    ScrollAndInvalidate(TopRow - 1);                           return true;
            case KeyCode.CursorDown:  ScrollAndInvalidate(TopRow + 1);                           return true;
            case KeyCode.PageUp:      ScrollAndInvalidate(TopRow - pageH);                       return true;
            case KeyCode.PageDown:    ScrollAndInvalidate(TopRow + pageH);                       return true;
            case KeyCode.Home:        ScrollAndInvalidate(0);                                    return true;
            case KeyCode.End:         ScrollAndInvalidate(Math.Max(0, Lines - Viewport.Height)); return true;
            case KeyCode.CursorLeft:  ScrollAndInvalidate(LeftColumn - 1, false);                return true;
            case KeyCode.CursorRight: ScrollAndInvalidate(LeftColumn + 1, false);                return true;
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

    protected override bool OnMouseEvent(MouseEventArgs ev)
    {
        if (IsContextMenuMouseEvent(ev))
        {
            ev.Handled = true;
            return true;
        }

        return base.OnMouseEvent(ev);
    }

    private void ScrollAndInvalidate(int topRow)
    {
        if (IsSelecting) IsSelecting = false; // plain navigation cancels any active selection
        if (!CanScrollVertical())
        {
            ScrollTo(0);
        }
        else
        {
            int maxTopRow = Math.Max(0, GetContentLineCount() - Viewport.Height);
            ScrollTo(Math.Clamp(topRow, 0, maxTopRow));
        }
        SetNeedsDraw();
        SuperView?.SetNeedsDraw();
    }

    private void ScrollAndInvalidate(int leftColumn, bool vertical)
    {
        if (!CanScrollHorizontal())
        {
            ScrollTo(0, false);
        }
        else
        {
            int maxLeftColumn = Math.Max(0, GetMaxLineWidth() - Viewport.Width);
            ScrollTo(Math.Clamp(leftColumn, 0, maxLeftColumn), vertical);
        }
        SetNeedsDraw();
        SuperView?.SetNeedsDraw();
    }

    public bool CanScrollVertical() =>
        GetContentLineCount() > Math.Max(1, Viewport.Height);

    private bool CanScrollHorizontal() =>
        GetMaxLineWidth() > Math.Max(1, Viewport.Width);

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
            width += Math.Max(1, rune.GetColumns());
        return width;
    }

    private bool IsContextMenuMouseEvent(MouseEventArgs ev) =>
        ev.Flags == ContextMenu?.MouseFlags
        || ev.Flags.HasFlag(MouseFlags.Button3Pressed)
        || ev.Flags.HasFlag(MouseFlags.Button3Released)
        || ev.Flags.HasFlag(MouseFlags.Button3Clicked)
        || ev.Flags.HasFlag(MouseFlags.Button3DoubleClicked)
        || ev.Flags.HasFlag(MouseFlags.Button3TripleClicked);

    protected override bool OnDrawingContent()
    {
        var handled = base.OnDrawingContent();
        if (ShowScrollBars)
            DrawVerticalScrollBar();
        return handled;
    }

    private void DrawVerticalScrollBar()
    {
        if (Application.Driver is null) return;

        int height = Viewport.Height;
        int width = Viewport.Width;
        if (height <= 0 || width <= 0 || Lines <= height) return;

        if (height == 1)
        {
            Application.Driver.SetAttribute(GetNormalColor());
            Move(width - 1, 0);
            Application.Driver.AddRune(new System.Text.Rune('░'));
            return;
        }

        Application.Driver.SetAttribute(GetNormalColor());
        Move(width - 1, 0);
        Application.Driver.AddRune(new System.Text.Rune('▲'));
        if (height == 2)
        {
            Move(width - 1, 1);
            Application.Driver.AddRune(new System.Text.Rune('▼'));
            return;
        }

        int trackHeight = height - 2;
        int thumbHeight = Math.Max(1, trackHeight * height / Math.Max(1, Lines));
        int maxTop = Math.Max(1, Lines - height);
        int thumbTop = 1 + Math.Min(trackHeight - thumbHeight, TopRow * (trackHeight - thumbHeight) / maxTop);

        for (int y = 1; y < height - 1; y++)
        {
            Move(width - 1, y);
            var ch = y >= thumbTop && y < thumbTop + thumbHeight ? '█' : '░';
            Application.Driver.AddRune(new System.Text.Rune(ch));
        }
        Move(width - 1, height - 1);
        Application.Driver.AddRune(new System.Text.Rune('▼'));
    }
}
