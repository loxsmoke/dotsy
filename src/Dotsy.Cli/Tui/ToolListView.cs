using System.Drawing;
using System.Text;
using Terminal.Gui;
using TGAttribute = Terminal.Gui.Attribute;

namespace Dotsy.Cli.Tui;

// ListView that adds left/right horizontal scrolling via a per-panel offset.
// Home/End scroll to start/end of selected line; Ctrl+Home/End jump to first/last item.
// Ctrl+Left/Right scroll by 10 characters. Selection change resets the offset.
internal sealed class ToolListView : ListView
{
    private int _hScrollOffset;

    // Lets the bracket renderer read each row's group so consecutive tool calls from the same
    // prompt can be drawn with a half-frame gutter. Set by AgentWindow.
    public Func<int, ToolRow?>? RowGetter { get; set; }

    public ToolListView()
    {
        KeyBindings.Add(Key.Home.WithCtrl, Command.Start);
        KeyBindings.Add(Key.End.WithCtrl, Command.End);
        SelectedItemChanged += (_, _) => _hScrollOffset = 0;
    }

    protected override bool OnKeyDown(Key key)
    {
        if (key.IsAlt) return false;
        if (key == Key.CursorLeft.WithCtrl)
        {
            _hScrollOffset = Math.Max(0, _hScrollOffset - 10);
            SetNeedsDraw();
            return true;
        }
        if (key == Key.CursorRight.WithCtrl)
        {
            _hScrollOffset += 10;
            SetNeedsDraw();
            return true;
        }

        switch (key.KeyCode)
        {
            case KeyCode.Home:
                _hScrollOffset = 0;
                SetNeedsDraw();
                return true;
            case KeyCode.End:
                if (Source is not null && SelectedItem >= 0 && SelectedItem < Source.Count)
                {
                    var text = Source.ToList()[SelectedItem]?.ToString() ?? "";
                    _hScrollOffset = Math.Max(0, text.Length - Viewport.Width);
                }
                SetNeedsDraw();
                return true;
            case KeyCode.CursorLeft:
                if (_hScrollOffset > 0) { _hScrollOffset--; SetNeedsDraw(); }
                return true;
            case KeyCode.CursorRight:
                _hScrollOffset++;
                SetNeedsDraw();
                return true;
        }
        return base.OnKeyDown(key);
    }

    protected override bool OnDrawingContent()
    {
        bool handled;
        if (_hScrollOffset == 0)
        {
            handled = base.OnDrawingContent();
        }
        else
        {
            Rectangle f = Viewport;
            int renderWidth = Math.Max(0, f.Width - 1);
            int itemIdx = TopItem;

            for (int row = 0; row < f.Height; row++, itemIdx++)
            {
                bool isSelected = itemIdx == SelectedItem;
                var color = HasFocus
                    ? isSelected ? ColorScheme!.Focus : GetNormalColor()
                    : isSelected ? ColorScheme!.HotNormal : GetNormalColor();
                SetAttribute(color);
                Move(0, row);

                if (Source is null || itemIdx >= Source.Count)
                {
                    for (int c = 0; c < f.Width; c++) Driver?.AddRune((Rune)' ');
                    continue;
                }

                var rowArgs = new ListViewRowEventArgs(itemIdx);
                OnRowRender(rowArgs);
                if (rowArgs.RowAttribute is { } ra) SetAttribute(ra);

                Source.Render(this, isSelected, itemIdx, 0, row, renderWidth, _hScrollOffset);
            }
            handled = true;
        }

        DrawGroupBrackets();
        DrawVerticalScrollBar();
        return handled;
    }

    // Draws a dim half-frame gutter in column 0 (just left of the status icon) that visually
    // brackets the consecutive tool calls belonging to one prompt: ┌ on the first, │ on the
    // middle ones, └ on the last. A lone tool gets no bracket. Drawn a shade dimmer than the
    // panel border so it reads as a secondary, structural element.
    private void DrawGroupBrackets()
    {
        if (RowGetter is null || Driver is null) return;

        int count = Source?.Count ?? 0;
        int height = Viewport.Height;
        for (int row = 0; row < height; row++)
        {
            int idx = TopItem + row;
            if (idx < 0 || idx >= count) continue;

            var glyph = GroupGlyph(idx, count);
            if (glyph == '\0') continue;

            bool selHi = HasFocus && idx == SelectedItem;
            SetAttribute(selHi
                ? new TGAttribute(ColorName16.Gray, ColorName16.DarkGray)
                : new TGAttribute(ColorName16.DarkGray, ColorName16.Black));
            Move(0, row);
            Driver.AddRune(new Rune(glyph));
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

        if (!prevSame && !nextSame) return '\0'; // single tool — no half-frame
        if (!prevSame)              return '┌';
        if (!nextSame)              return '└';
        return '│';
    }

    private void DrawVerticalScrollBar()
    {
        if (Application.Driver is null || Source is null) return;

        int height = Viewport.Height;
        int width = Viewport.Width;
        int count = Source.Count;
        if (height <= 0 || width <= 0 || count <= height) return;

        Application.Driver.SetAttribute(GetNormalColor());
        if (height == 1)
        {
            Move(width - 1, 0);
            Application.Driver.AddRune(new Rune('░'));
            return;
        }

        Move(width - 1, 0);
        Application.Driver.AddRune(new Rune('▲'));
        if (height == 2)
        {
            Move(width - 1, 1);
            Application.Driver.AddRune(new Rune('▼'));
            return;
        }

        int trackHeight = height - 2;
        int thumbHeight = Math.Max(1, trackHeight * height / Math.Max(1, count));
        int maxTop = Math.Max(1, count - height);
        int thumbTop = 1 + Math.Min(trackHeight - thumbHeight, TopItem * (trackHeight - thumbHeight) / maxTop);

        for (int y = 1; y < height - 1; y++)
        {
            Move(width - 1, y);
            var ch = y >= thumbTop && y < thumbTop + thumbHeight ? '█' : '░';
            Application.Driver.AddRune(new Rune(ch));
        }
        Move(width - 1, height - 1);
        Application.Driver.AddRune(new Rune('▼'));
    }
}
