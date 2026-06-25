namespace Dotsy.Cli.Tui;

// Pure scrolling arithmetic, extracted so scroll/paging behaviour can be unit-tested without a
// console. All functions are axis-agnostic: pass line-count + viewport height for vertical scroll,
// or max line width + viewport width for horizontal scroll.
internal static class ScrollMath
{
    // The largest valid scroll offset — the "scroll to end" position where the last page of content
    // exactly fills the viewport. This is the bottom (vertical) / right-most (horizontal) extreme.
    public static int MaxOffset(int contentLength, int viewportSpan) =>
        Math.Max(0, contentLength - viewportSpan);

    // Clamps a desired offset into the valid [0, MaxOffset] range.
    public static int ClampOffset(int desiredOffset, int contentLength, int viewportSpan) =>
        Math.Clamp(desiredOffset, 0, MaxOffset(contentLength, viewportSpan));

    // Whether the content is larger than the viewport, i.e. scrolling is meaningful at all.
    public static bool CanScroll(int contentLength, int viewportSpan) =>
        contentLength > Math.Max(1, viewportSpan);

    // Lines/columns moved per page: one viewport minus a row of overlap for reading continuity,
    // never less than 1 (so paging always advances even in a 1-row viewport).
    public static int PageStep(int viewportSpan) =>
        Math.Max(1, viewportSpan - 1);

    // Top offset after a PageUp/PageDown from currentTop, clamped to the valid range.
    public static int PageTop(int currentTop, int viewportHeight, int contentLineCount, bool down)
    {
        int target = currentTop + (down ? PageStep(viewportHeight) : -PageStep(viewportHeight));
        return ClampOffset(target, contentLineCount, viewportHeight);
    }

    // Top offset adjusted just enough to keep the selected item within the visible span.
    public static int EnsureVisibleTop(int currentTop, int selectedIndex, int viewportHeight, int contentLineCount)
    {
        int span = Math.Max(1, viewportHeight);
        int selected = Math.Clamp(selectedIndex, 0, Math.Max(0, contentLineCount - 1));
        int top = ClampOffset(currentTop, contentLineCount, span);

        if (selected < top)
            return selected;

        int bottom = top + span - 1;
        if (selected > bottom)
            return ClampOffset(selected - span + 1, contentLineCount, span);

        return top;
    }
}
