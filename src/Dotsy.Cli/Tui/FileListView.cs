using System.Drawing;
using Terminal.Gui;
using TGAttribute = Terminal.Gui.Attribute;

namespace Dotsy.Cli.Tui;

// ListView that draws file-change rows with per-segment colours and path truncation.
internal sealed class FileListView : ListView
{
    public Func<int, FileRow?>? RowGetter { get; set; }

    protected override bool OnKeyDown(Key key)
    {
        if (key.IsAlt) return false;
        // Consume left/right so they don't escape to Toplevel and steal focus
        if (key.KeyCode == KeyCode.CursorLeft || key.KeyCode == KeyCode.CursorRight)
            return true;
        return base.OnKeyDown(key);
    }

    protected override bool OnDrawingContent()
    {
        if (RowGetter is null) return base.OnDrawingContent();
        Rectangle viewport = Viewport;
        int count = Source?.Count ?? 0;
        for (int row = 0; row < viewport.Height; row++)
        {
            int idx = TopItem + row;
            Move(0, row);
            bool sel = HasFocus && idx == SelectedItem;
            DrawFileRow(idx < count ? RowGetter(idx) : null, sel, viewport.Width);
        }
        return true;
    }

    private static void DrawFileRow(FileRow? item, bool sel, int width)
    {
        var bg     = sel ? ColorName16.DarkGray : ColorName16.Black;
        var normAt = new TGAttribute(sel ? ColorName16.White : ColorName16.Gray,        bg);
        var pathAt = new TGAttribute(ColorName16.White,                                  bg);
        var addAt  = new TGAttribute(sel ? ColorName16.White : ColorName16.BrightGreen,  bg);
        var delAt  = new TGAttribute(sel ? ColorName16.White : ColorName16.BrightRed,    bg);
        int col = 0;

        void Str(string s, TGAttribute a)
        {
            Application.Driver!.SetAttribute(a);
            foreach (var ch in s) { if (col >= width) return; Application.Driver.AddRune(new System.Text.Rune(ch)); col++; }
        }

        if (item is null) { Str(new string(' ', width), normAt); return; }

        const int PrefixLen = 4; // "  X " prefix

        switch (item.ChangeType)
        {
            case FileChangeType.Added:
            {
                var path = TruncatePath(item.Path, width - PrefixLen);
                Str("  + ", addAt); Str(path, pathAt);
                break;
            }
            case FileChangeType.Deleted:
            {
                var path = TruncatePath(item.Path, width - PrefixLen);
                Str("  - ", delAt); Str(path, pathAt);
                break;
            }
            default:
            {
                // Reserve trailing space for "+N  -N" stats so they're always visible
                var statsStr = $"   +{item.Added}  -{item.Deleted}";
                int availForPath = width - PrefixLen - statsStr.Length;
                var path = availForPath > 0 ? TruncatePath(item.Path, availForPath) : "";
                Str("  ↳ ", normAt); Str(path, pathAt);
                Str("   ",  normAt); Str($"+{item.Added}", addAt);
                Str("  ",   normAt); Str($"-{item.Deleted}", delAt);
                break;
            }
        }

        Application.Driver!.SetAttribute(normAt);
        while (col < width) { Application.Driver.AddRune(new System.Text.Rune(' ')); col++; }
    }

    // Shorten path by replacing middle directory segments with "...".
    // Preserves the filename (last segment) and as many leading dirs as fit.
    // Falls back to clipping with a trailing "..." so truncation is always visible.
    private static string TruncatePath(string path, int maxWidth)
    {
        if (maxWidth <= 0) return "";
        if (path.Length <= maxWidth) return path;

        var parts = path.Split('/');
        if (parts.Length < 3)
            return Clip(path, maxWidth);

        // Try keeping N leading dirs + "/.../" + filename, from most to fewest leading dirs
        for (int n = parts.Length - 2; n >= 1; n--)
        {
            var candidate = string.Join("/", parts[..n]) + "/.../" + parts[^1];
            if (candidate.Length <= maxWidth) return candidate;
        }

        // Even "first/.../filename" doesn't fit — show as much of the filename as possible
        var minimal = ".../" + parts[^1];
        return minimal.Length <= maxWidth ? minimal : Clip(minimal, maxWidth);
    }

    // Clip string to maxWidth, adding trailing "..." to make truncation visible.
    private static string Clip(string s, int maxWidth) =>
        maxWidth > 3 ? s[..(maxWidth - 3)] + "..." : s[..maxWidth];
}
