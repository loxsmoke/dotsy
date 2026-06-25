using System.Drawing;
using Dotsy.Cli.Tui.Colors;

namespace Dotsy.Cli.Tui.FileList;

// ListView that draws file-change rows with per-segment colours and path truncation.
internal sealed class FileListView : ListView
{
    private int topItem;

    public Func<int, FileRow?>? RowGetter { get; set; }

    public FileListView()
    {
        ValueChanged += (_, _) => EnsureSelectedItemVisibleCompat();
    }

    public int TopItemCompat
    {
        get => topItem;
        set
        {
            topItem = Math.Max(0, value);
            SetNeedsDraw();
        }
    }

    protected override bool OnKeyDown(Key key)
    {
        if (key.IsAlt) return false;
        // Consume left/right so they don't escape to Toplevel and steal focus
        if (key.KeyCode == KeyCode.CursorLeft || key.KeyCode == KeyCode.CursorRight)
            return true;
        return base.OnKeyDown(key);
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        if (RowGetter is null) return base.OnDrawingContent(context);
        EnsureSelectedItemVisibleCompat();
        Rectangle viewport = Viewport;
        int count = Source?.Count ?? 0;
        for (int row = 0; row < viewport.Height; row++)
        {
            Move(0, row);
            int idx = topItem + row;
            bool isSelected = HasFocus && idx == SelectedItem;
            DrawFileRow(idx < count ? RowGetter(idx) : null, isSelected, viewport.Width);
        }

        if (Source is not null)
        {
            ScrollBar.DrawVertical(this, Viewport.Width - 1, 0, Viewport.Height, Source.Count, Viewport.Height, topItem);
        }
        return true;
    }

    private void EnsureSelectedItemVisibleCompat()
    {
        if (SelectedItem is not { } selected)
            return;

        topItem = ScrollMath.EnsureVisibleTop(topItem, selected, Viewport.Height, Source?.Count ?? 0);
    }

    private static void DrawFileRow(FileRow? item, bool isSelected, int width)
    {
        // A selected row draws entirely in the selection highlight; otherwise each segment keeps
        // its semantic theme colour (normal text, bright path, green additions, red deletions).
        var normAt = isSelected ? Palette.SelRow : Palette.Normal;
        var pathAt = isSelected ? Palette.SelRow : Palette.Bright;
        var addAt  = isSelected ? Palette.SelRow : Palette.Success;
        var delAt  = isSelected ? Palette.SelRow : Palette.Err;
        int col = 0;

        void Str(string s, TGAttribute a)
        {
            TuiSessionContext.App.Driver!.SetAttribute(a);
            // Iterate runes, not chars: new Rune(char) throws on a surrogate half (astral emoji
            // in a filename). EnumerateRunes pairs surrogates / substitutes U+FFFD.
            foreach (var rune in s.EnumerateRunes())
            {
                if (Glyphs.GetColumns(rune) <= 0) continue;       // skip zero-width (desyncs columns)
                if (col >= width) return;
                TuiSessionContext.App.Driver.AddRune(Glyphs.Safe(rune)); // replace emoji (2-col vs 1-col mismatch)
                col++;
            }
        }

        if (item is null) 
        { 
            Str(new string(' ', width), normAt); 
            return;
        }

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
                Str("  \u21b3 ", normAt); Str(path, pathAt);
                Str("   ",  normAt); Str($"+{item.Added}", addAt);
                Str("  ",   normAt); Str($"-{item.Deleted}", delAt);
                break;
            }
        }

        TuiSessionContext.App.Driver!.SetAttribute(normAt);
        while (col < width) { TuiSessionContext.App.Driver.AddRune(new System.Text.Rune(' ')); col++; }
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

        // Even "first/.../filename" doesn't fit: show as much of the filename as possible
        var minimal = ".../" + parts[^1];
        return minimal.Length <= maxWidth ? minimal : Clip(minimal, maxWidth);
    }

    // Clip string to maxWidth, adding trailing "..." to make truncation visible.
    private static string Clip(string s, int maxWidth) =>
        maxWidth > 3 ? s[..(maxWidth - 3)] + "..." : s[..maxWidth];
}
