using System.Drawing;
using System.Text;
using Dotsy.Cli.Tui.Colors;

namespace Dotsy.Cli.Tui;

internal sealed class CompletionListView : ListView
{
    private int topItem;

    public Func<Key, bool>? EditKeyHandler { get; set; }

    public CompletionListView()
    {
        ValueChanged += (_, _) => EnsureSelectedItemVisibleCompat();
    }

    protected override bool OnKeyDown(Key key)
    {
        if (key.IsAlt) return false;

        switch (key.KeyCode)
        {
            case KeyCode.CursorUp:
                MoveSelection(-1);
                return true;
            case KeyCode.CursorDown:
                MoveSelection(1);
                return true;
            case KeyCode.Home:
                SetSelection(0);
                return true;
            case KeyCode.End:
                SetSelection((Source?.Count ?? 1) - 1);
                return true;
            case KeyCode.PageUp:
                MoveSelection(-ScrollMath.PageStep(Viewport.Height));
                return true;
            case KeyCode.PageDown:
                MoveSelection(ScrollMath.PageStep(Viewport.Height));
                return true;
        }

        if (key.KeyCode == KeyCode.Backspace
            || (!key.IsCtrl && !key.IsAlt && key.AsRune.Value >= 32))
            return EditKeyHandler?.Invoke(key) ?? false;

        return base.OnKeyDown(key);
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        EnsureSelectedItemVisibleCompat();

        var viewport = Viewport;
        var count = Source?.Count ?? 0;
        var showScrollBar = count > viewport.Height;
        var renderWidth = Math.Max(0, viewport.Width - (showScrollBar ? 1 : 0));

        for (var row = 0; row < viewport.Height; row++)
        {
            var itemIdx = topItem + row;
            var isSelected = itemIdx == SelectedItem.GetValueOrDefault(-1);
            var attribute = isSelected
                ? Palette.BtnScheme().Focus
                : Palette.Bright;

            SetAttribute(attribute);
            Move(0, row);

            if (Source is null || itemIdx < 0 || itemIdx >= count)
            {
                FillRow(viewport.Width);
                continue;
            }

            DrawText(Source.ToList()[itemIdx]?.ToString() ?? "", renderWidth, attribute);
            FillRemainder(renderWidth, row, viewport.Width - renderWidth);
        }

        if (showScrollBar)
            ScrollBar.DrawVertical(this, viewport.Width - 1, 0, viewport.Height, count, viewport.Height, topItem);

        return true;
    }

    private void MoveSelection(int delta) =>
        SetSelection(SelectedItem.GetValueOrDefault(0) + delta);

    private void SetSelection(int value)
    {
        var count = Source?.Count ?? 0;
        if (count <= 0) return;

        SelectedItem = Math.Clamp(value, 0, count - 1);
        EnsureSelectedItemVisibleCompat();
        SetNeedsDraw();
    }

    private void EnsureSelectedItemVisibleCompat()
    {
        if (SelectedItem is not { } selected)
            return;

        topItem = ScrollMath.EnsureVisibleTop(topItem, selected, Viewport.Height, Source?.Count ?? 0);
    }

    private void FillRemainder(int x, int y, int count)
    {
        if (count <= 0 || TuiSessionContext.App.Driver is null) return;

        Move(x, y);
        FillRow(count);
    }

    private static void FillRow(int width)
    {
        for (var i = 0; i < width; i++)
            TuiSessionContext.App.Driver?.AddRune(new Rune(' '));
    }

    private static void DrawText(string text, int width, TGAttribute attribute)
    {
        if (TuiSessionContext.App.Driver is null) return;

        TuiSessionContext.App.Driver.SetAttribute(attribute);
        var col = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (col >= width) break;
            if (Glyphs.GetColumns(rune) <= 0) continue;

            TuiSessionContext.App.Driver.AddRune(Glyphs.Safe(rune));
            col++;
        }

        while (col < width)
        {
            TuiSessionContext.App.Driver.AddRune(new Rune(' '));
            col++;
        }
    }
}
