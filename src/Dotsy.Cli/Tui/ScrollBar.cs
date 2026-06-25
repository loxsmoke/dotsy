using System.Text;

namespace Dotsy.Cli.Tui;

// Renderer for the minimal block-style scroll bars used by the list/text panels. The geometry
// lives in one pure, terminal-free method (<see cref="Build"/>) that returns the bar as a string
// of glyphs; the Draw* helpers just stamp that string down a column or across a row.
public static class ScrollBar
{
    private const char Track = '░';
    private const char Thumb = '█';
    private const char Up    = '▲';
    private const char Down  = '▼';
    private const char Left  = '◄';
    private const char Right = '►';

    /// <summary>
    /// Builds a scroll bar as a string of <paramref name="length"/> glyphs: a start cap, a
    /// proportional thumb (█) sitting on a track (░), then an end cap. Caps are ▲/▼ for a vertical
    /// bar and ◄/► for a horizontal one. <paramref name="viewportSpan"/> is how many units are
    /// visible, <paramref name="total"/> the total, <paramref name="offset"/> the first visible unit.
    /// Pure and deterministic — unit-testable without a terminal. Returns "" for non-positive length.
    /// </summary>
    public static string Build(bool horizontal, int length, int total, int viewportSpan, int offset)
    {
        if (length <= 0) return string.Empty;

        char startCap = horizontal ? Left : Up;
        char endCap   = horizontal ? Right : Down;

        if (length == 1) return Track.ToString();
        if (length == 2) return $"{startCap}{endCap}";

        int track = length - 2;
        var (thumbStart, thumbSize) = ThumbBounds(track, total, viewportSpan, offset);

        var sb = new StringBuilder(length);
        sb.Append(startCap);
        for (int i = 1; i <= track; i++)
            sb.Append(i >= thumbStart && i < thumbStart + thumbSize ? Thumb : Track);
        sb.Append(endCap);
        return sb.ToString();
    }

    // The bar takes the colour of the enclosing frame's border (the content view's SuperView), so it
    // tracks the frame's focus highlight: bright while the panel is focused, normal otherwise. Falls
    // back to the view's own scheme when it has no frame parent.
    private static TGAttribute BarColor(View view) =>
        (view.SuperView ?? view).GetScheme().Normal;

    /// <summary>Stamps a vertical scroll bar down the column at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public static void DrawVertical(View view, int x, int y, int length, int total, int viewportSpan, int offset)
    {
        if (TuiSessionContext.App.Driver is null || x < 0) return;
        var bar = Build(horizontal: false, length, total, viewportSpan, offset);
        TuiSessionContext.App.Driver.SetAttribute(BarColor(view));
        for (int i = 0; i < bar.Length; i++)
        {
            view.Move(x, y + i);
            TuiSessionContext.App.Driver.AddRune(new Rune(bar[i]));
        }
    }

    /// <summary>Stamps a horizontal scroll bar across the row at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public static void DrawHorizontal(View view, int x, int y, int length, int total, int viewportSpan, int offset)
    {
        if (TuiSessionContext.App.Driver is null || y < 0) return;
        var bar = Build(horizontal: true, length, total, viewportSpan, offset);
        TuiSessionContext.App.Driver.SetAttribute(BarColor(view));
        for (int i = 0; i < bar.Length; i++)
        {
            view.Move(x + i, y);
            TuiSessionContext.App.Driver.AddRune(new Rune(bar[i]));
        }
    }

    // Thumb size and 1-based start index within a track of `track` cells (the span between the caps).
    private static (int start, int size) ThumbBounds(int track, int total, int viewportSpan, int offset)
    {
        int size = Math.Max(1, track * viewportSpan / Math.Max(1, total));
        int maxOffset = Math.Max(1, total - viewportSpan);
        int start = 1 + Math.Min(track - size, offset * (track - size) / maxOffset);
        return (start, size);
    }
}
