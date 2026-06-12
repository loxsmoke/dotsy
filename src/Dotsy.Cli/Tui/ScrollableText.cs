using Terminal.Gui;

namespace Dotsy.Cli.Tui;

// Read-only scrollable TextView: accepts focus but blocks all editing keys.
internal sealed class ScrollableText : TextView
{
    public bool ShowScrollBars { get; set; } = true;

    public ScrollableText()
    {
        CanFocus = true;
        ReadOnly = true;
        WordWrap = false;
        ColorScheme = Palette.Scheme();
    }

    protected override bool OnKeyDown(Key key)
    {
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

    private bool CanScrollVertical() =>
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
            .Select(line => line.TrimEnd('\r').Length)
            .DefaultIfEmpty(0)
            .Max();
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
