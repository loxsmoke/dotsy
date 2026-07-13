using System.Drawing;
using Dotsy.Cli.Tui.Colors;

namespace Dotsy.Cli.Tui;

public partial class AgentWindow
{
    // -- Private helpers -------------------------------------------------------------

    // F2 in the tool panel: jump the conversation to the user prompt that started the selected
    // tool call's group, and move focus there.
    private void JumpToSelectedToolGroupOrigin()
    {
        if (toolCallList.SelectedItem is not { } idx || idx < 0 || idx >= toolCallRows.Count)
            return;

        var group = toolCallRows[idx].Group;
        if (group == 0 || !groupConvoLine.TryGetValue(group, out var logicalLine))
            return;

        FocusConvoAtLogicalLine(logicalLine);
    }

    // Scrolls the conversation panel so the given logical line sits at the top of the viewport (or
    // as close as the end of the buffer allows) and moves focus to it.
    private void FocusConvoAtLogicalLine(int logicalLine)
    {
        if (logicalLine < 0 || logicalLine >= conversationLines.Count)
            return;

        int width = convo.Frame.Width > 0 ? convo.Frame.Width : TuiSessionContext.App.Driver?.Cols ?? 80;
        if (convo.CanScrollVertical())
            width = Math.Max(1, width - 1);

        // Logical lines wrap into a variable number of display rows, so walk the buffer to convert
        // the target logical line into a display-row offset (and total rows, to clamp the scroll).
        int targetRow = 0;
        int totalRows = 0;
        for (int i = 0; i < conversationLines.Count; i++)
        {
            if (i == logicalLine)
                targetRow = totalRows;
            totalRows += noWrapLineIndices.Contains(i) ? 1 : WrapCellLine(conversationLines[i], width).Count;
        }

        int maxTop = Math.Max(0, totalRows - convo.Viewport.Height);
        convo.ScrollTo(new Point(0, Math.Min(targetRow, maxTop)));
        convo.SetFocus();
    }

    private void AppendConvoError(string message)
    {
        var lines = message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        AppendConvo("\n", Palette.Normal, false);
        foreach (var line in lines)
            AppendConvo($"  {line}\n", Palette.Err, false);
        AppendConvo("\n", Palette.Normal);
    }
    private void AppendConvo(string text, TGAttribute attr, bool reload = true)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value == '\n') { conversationLines.Add([]); continue; }
            if (rune.Value == '\r' || Glyphs.GetColumns(rune) <= 0) continue; // drop CR + zero-width (avoids grid desync)
            conversationLines[^1].Add(new Cell(attr, false, Glyphs.Safe(rune).ToString()));
        }
        if (reload) ScheduleReloadConvo();
    }

    // Coalesces convo reloads. ReloadConvo rebuilds and re-wraps the *entire*
    // conversation, so calling it once per AppendConvo makes a burst of appends O(n²)
    // and saturates the UI thread - which is what makes the screen look frozen under
    // verbose mode (and fast streaming). Instead we queue a single reload for the next
    // main-loop iteration; the many appends that arrive within one iteration collapse
    // into one rebuild that reflects all of them.
    //
    // We deliberately use AddTimeout(Zero) rather than App.Invoke. AppendConvo already
    // runs on the main UI thread (callers marshal onto it), and Invoke executes
    // *synchronously* when called from the main thread - that would reload on every
    // append and defeat the batching. A zero timeout always defers to the next
    // iteration, which is what lets the dedup flag actually collapse the burst.
    private void ScheduleReloadConvo()
    {
        if (convoReloadScheduled) return;
        convoReloadScheduled = true;
        TuiSessionContext.App.AddTimeout(TimeSpan.Zero, () =>
        {
            convoReloadScheduled = false;
            ReloadConvo();
            return false; // one-shot
        });
    }

    private void ReloadConvo()
    {
        var lines = BuildConvoSnapshot();
        // Collapse any active selection before loading new text to avoid stale cursor state.
        convo.ClearSelection();
        convo.LoadText(lines);
        convo.MoveEnd();
    }
    
    // Drops the incremental wrap cache so the next reload rebuilds every line. Call this
    // whenever a previously-rendered (immutable) line's content or colour changes - i.e. on
    // clear and on theme recolor - since the cache assumes those lines never change.
    private void InvalidateConvoCache()
    {
        convoCachedRows.Clear();
        convoCachedWidth = -1;
        convoCachedLogical = 0;
        convoCachedTailRows = 0;
    }

    private List<List<Cell>> BuildConvoSnapshot()
    {
        // Effective displayed line count: trailing empty logical lines are not shown.
        var lineCount = conversationLines.Count;
        while (lineCount > 1 && conversationLines[lineCount - 1].Count == 0)
            lineCount--;

        int width = convo.Frame.Width > 0 ? convo.Frame.Width : TuiSessionContext.App.Driver?.Cols ?? 80;

        // Account for potential vertical scrollbar which consumes 1 column on the right
        if (convo.CanScrollVertical())
            width = Math.Max(1, width - 1);

        bool cursorVisible;
        lock (streamCursorLock)
            cursorVisible = streamCursorVisible;

        // The last displayed line is the "tail": it is still mutable and carries the stream
        // cursor, so it is re-wrapped on every reload. Lines [0, stableTarget) are immutable
        // and their wrapped rows are reused from the cache.
        int stableTarget = lineCount - 1;

        // Rebuild from scratch when the width changed or the cache somehow ran ahead of the
        // current stable region (e.g. after a clear without invalidation).
        if (width != convoCachedWidth || convoCachedLogical > stableTarget)
        {
            convoCachedRows.Clear();
            convoCachedLogical = 0;
            convoCachedTailRows = 0;
            convoCachedWidth = width;
        }

        // Remove the previously-appended tail rows so we can extend the stable prefix and
        // re-append a freshly wrapped tail.
        if (convoCachedTailRows > 0)
        {
            convoCachedRows.RemoveRange(convoCachedRows.Count - convoCachedTailRows, convoCachedTailRows);
            convoCachedTailRows = 0;
        }

        // Extend the cached prefix over any logical lines that have since become immutable.
        for (int i = convoCachedLogical; i < stableTarget; i++)
            AppendWrappedLine(convoCachedRows, conversationLines[i], i, width);
        if (stableTarget > convoCachedLogical)
            convoCachedLogical = stableTarget;

        // Append the freshly-wrapped tail (with the stream cursor if visible).
        int tailIndex = lineCount - 1;
        var tail = conversationLines[tailIndex];
        if (cursorVisible)
            tail = WithStreamCursor(tail);

        int before = convoCachedRows.Count;
        AppendWrappedLine(convoCachedRows, tail, tailIndex, width);
        convoCachedTailRows = convoCachedRows.Count - before;

        if (convoCachedRows.Count == 0) convoCachedRows.Add([]);

        return convoCachedRows;
    }

    // Wraps one logical line into display rows and appends them to dest. Lines flagged in
    // noWrapLineIndices are emitted verbatim (single row, never wrapped).
    private void AppendWrappedLine(List<List<Cell>> dest, List<Cell> line, int logicalIndex, int width)
    {
        if (noWrapLineIndices.Contains(logicalIndex))
            dest.Add([.. line]);
        else
            dest.AddRange(WrapCellLine(line, width));
    }

    internal static List<Cell> WithStreamCursor(List<Cell> line) =>
        [.. line, new Cell(Palette.Bright, false, "\u258c")];
    // Display columns a cell occupies, per Terminal.Gui's own renderer (which advances the cursor
    // by Rune.GetColumns()). Wrapping uses this so wrapped lines match what TG actually draws. Emoji
    // are replaced with 1-column glyphs at cell creation (Glyphs.Safe), so every cell here has a
    // width TG and the terminal agree on; clamp to >= 1 as a guard.
    private static int CellColumns(Cell cell) => Math.Max(1, Glyphs.GetColumns(CellRune(cell)));

    private static System.Text.Rune CellRune(Cell cell)
    {
        var runes = cell.Grapheme.EnumerateRunes();
        return runes.MoveNext() ? runes.Current : new System.Text.Rune(' ');
    }

    // Word-wraps a single logical line (list of colored cells) into display lines at `width`
    // DISPLAY COLUMNS (not cell count). Preserves per-cell attributes exactly: no color bleed.
    internal static List<List<Cell>> WrapCellLine(List<Cell> cells, int width)
    {
        if (cells.Count == 0) return [[]];
        if (width < 1) width = 1;

        int totalCols = 0;
        foreach (var c in cells) totalCols += CellColumns(c);
        if (totalCols <= width) return [new List<Cell>(cells)];

        var result = new List<List<Cell>>();
        var current = new List<Cell>();
        int curCols = 0;

        static Cell Copy(Cell c) => new(c.Attribute, false, c.Grapheme);

        int i = 0;
        while (i < cells.Count)
        {
            bool isSpace = CellRune(cells[i]).Value == ' ';
            int tokenStart = i;
            while (i < cells.Count && (CellRune(cells[i]).Value == ' ') == isSpace) i++;

            int tokenCols = 0;
            for (int j = tokenStart; j < i; j++) tokenCols += CellColumns(cells[j]);

            if (isSpace)
            {
                for (int j = tokenStart; j < i; j++)
                {
                    int cw = CellColumns(cells[j]);
                    if (curCols > 0 && curCols + cw > width) { result.Add(current); current = new List<Cell>(); curCols = 0; }
                    current.Add(Copy(cells[j]));
                    curCols += cw;
                }
            }
            else if (curCols > 0 && curCols + tokenCols <= width)
            {
                // Word fits on the current line.
                for (int j = tokenStart; j < i; j++) { current.Add(Copy(cells[j])); curCols += CellColumns(cells[j]); }
            }
            else
            {
                // Word doesn't fit: flush (stripping trailing spaces), then hard-wrap the word by columns.
                if (curCols > 0)
                {
                    if (current.Any(c => CellRune(c).Value != ' '))
                        while (current.Count > 0 && CellRune(current[^1]).Value == ' ') current.RemoveAt(current.Count - 1);
                    if (current.Count > 0)
                        result.Add(current);
                    current = new List<Cell>();
                    curCols = 0;
                }
                for (int j = tokenStart; j < i; j++)
                {
                    int cw = CellColumns(cells[j]);
                    if (curCols > 0 && curCols + cw > width) { result.Add(current); current = new List<Cell>(); curCols = 0; }
                    current.Add(Copy(cells[j]));
                    curCols += cw;
                }
            }
        }

        while (current.Count > 0 && CellRune(current[^1]).Value == ' ')
            current.RemoveAt(current.Count - 1);
        result.Add(current);

        return result;
    }
}
