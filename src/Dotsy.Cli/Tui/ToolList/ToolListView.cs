using System.Drawing;
using System.Text;
using Dotsy.Cli.Tui;
using Dotsy.Cli.Tui.Colors;

namespace Dotsy.Cli.Tui.ToolList;

// ListView that adds left/right horizontal scrolling via a per-panel offset.
// Home/End scroll to start/end of selected line; Ctrl+Home/End jump to first/last item.
// Ctrl+Left/Right scroll by 10 characters. Selection change resets the offset.
internal sealed class ToolListView : ListView
{
    private int horizontalScrollOffset;

    // Lets the bracket renderer read each row's group so consecutive tool calls from the same
    // prompt can be drawn with a half-frame gutter. Set by AgentWindow.
    public Func<int, ToolRow?>? RowGetter { get; set; }

    // Reads/writes the base view's live scroll position (Viewport.Y). Using the base position —
    // instead of a private copy — keeps the group brackets and scrollbar in sync when the user
    // scrolls interactively (the private copy went stale, so they never moved).
    public int TopItemCompat
    {
        get => Viewport.Y;
        set
        {
            Viewport = Viewport with { Y = Math.Max(0, value) };
            SetNeedsDraw();
        }
    }

    public ToolListView()
    {
        ValueChanged += (_, _) => horizontalScrollOffset = 0;
    }

    protected override bool OnKeyDown(Key key)
    {
        if (key.IsAlt) return false;
        if (key == Key.CursorLeft.WithCtrl)
        {
            horizontalScrollOffset = Math.Max(0, horizontalScrollOffset - 10);
            SetNeedsDraw();
            return true;
        }
        if (key == Key.CursorRight.WithCtrl)
        {
            horizontalScrollOffset += 10;
            SetNeedsDraw();
            return true;
        }

        switch (key.KeyCode)
        {
            case KeyCode.Home:
                horizontalScrollOffset = 0;
                SetNeedsDraw();
                return true;
            case KeyCode.End:
                var selectedItem = SelectedItem.GetValueOrDefault(-1);
                if (Source is not null && selectedItem >= 0 && selectedItem < Source.Count)
                {
                    var text = Source.ToList()[selectedItem]?.ToString() ?? "";
                    horizontalScrollOffset = ScrollMath.MaxOffset(text.Length, Viewport.Width);
                }
                SetNeedsDraw();
                return true;
            case KeyCode.CursorLeft:
                if (horizontalScrollOffset > 0) { horizontalScrollOffset--; SetNeedsDraw(); }
                return true;
            case KeyCode.CursorRight:
                horizontalScrollOffset++;
                SetNeedsDraw();
                return true;
        }
        return base.OnKeyDown(key);
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        bool handled;
        if (horizontalScrollOffset == 0)
        {
            handled = base.OnDrawingContent(context);
        }
        else
        {
            Rectangle f = Viewport;
            int renderWidth = Math.Max(0, f.Width - 1);
            int itemIdx = Viewport.Y;

            for (int row = 0; row < f.Height; row++, itemIdx++)
            {
                bool isSelected = itemIdx == SelectedItem.GetValueOrDefault(-1);
                var color = HasFocus
                    ? isSelected ? GetScheme().Focus : GetScheme().Normal
                    : isSelected ? GetScheme().HotNormal : GetScheme().Normal;
                SetAttribute(color);
                Move(0, row);

                if (Source is null || itemIdx >= Source.Count)
                {
                    for (int c = 0; c < f.Width; c++) TuiSessionContext.App.Driver?.AddRune(new Rune(' '));
                    continue;
                }

                var rowArgs = new ListViewRowEventArgs(itemIdx);
                OnRowRender(rowArgs);
                if (rowArgs.RowAttribute is { } ra) SetAttribute(ra);

                Source.Render(this, isSelected, itemIdx, 0, row, renderWidth, horizontalScrollOffset);
            }
            handled = true;
        }

        DrawGroupBrackets();

        int count = Source?.Count ?? 0;
        if (count > Viewport.Height)
        {
            ScrollBar.DrawVertical(this, Viewport.Width - 1, 0, Viewport.Height, count, Viewport.Height, Viewport.Y);
        }
        return handled;
    }

    // Draws a dim half-frame gutter in column 0 (just left of the status icon) that visually
    // brackets the consecutive tool calls belonging to one prompt. A lone tool gets no bracket.
    // Drawn a shade dimmer than the
    // panel border so it reads as a secondary, structural element.
    private void DrawGroupBrackets()
    {
        if (RowGetter is null || TuiSessionContext.App.Driver is null) return;

        int count = Source?.Count ?? 0;
        int height = Viewport.Height;
        for (int row = 0; row < height; row++)
        {
            int idx = Viewport.Y + row;
            if (idx < 0 || idx >= count) continue;

            var glyph = GroupGlyph(idx, count);
            if (glyph == '\0') continue;

            bool isSelected = idx == SelectedItem.GetValueOrDefault(-1);
            var rowColor = HasFocus
                ? isSelected ? GetScheme().Focus : GetScheme().Normal
                : isSelected ? GetScheme().HotNormal : GetScheme().Normal;

            var fgColor = (HasFocus && isSelected) ? Palette.Normal.Foreground : Palette.Dim.Foreground;
            SetAttribute(new TGAttribute(fgColor, rowColor.Background));
            Move(0, row);
            TuiSessionContext.App.Driver.AddRune(new Rune(glyph));
        }
    }

    // '\0' = no bracket (ungrouped row, or the only tool in its group).
    private char GroupGlyph(int idx, int count)
    {
        var cur = RowGetter!(idx);
        if (cur is null || cur.Group == 0) return '\0';

        int g = cur.Group;
        bool prevSame = idx > 0          && RowGetter(idx - 1)?.Group == g;
        bool nextSame = idx < count - 1  && RowGetter(idx + 1)?.Group == g;

        if (!prevSame && !nextSame) return '\0';
        if (!prevSame)              return '\u250c';
        if (!nextSame)              return '\u2514';
        return '\u2502';
    }
}
