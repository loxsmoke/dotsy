using Dotsy.Cli.Tui.Colors;

namespace Dotsy.Cli.Tui;

public partial class AgentWindow
{
    // -- Private helpers -------------------------------------------------------------

    private void AppendConvoError(string message)
    {
        var lines = message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        AppendConvo("\n", Palette.Normal);
        foreach (var line in lines)
            AppendConvo($"  {line}\n", Palette.Err);
        AppendConvo("\n", Palette.Normal);
    }
    private void AppendConvo(string text, TGAttribute attr)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value == '\n') { conversationLines.Add([]); continue; }
            if (rune.Value == '\r' || Glyphs.GetColumns(rune) <= 0) continue; // drop CR + zero-width (avoids grid desync)
            conversationLines[^1].Add(new Cell(attr, false, Glyphs.Safe(rune).ToString()));
        }
        ReloadConvo();
    }

    private void ReloadConvo()
    {
        var lines = BuildConvoSnapshot();
        // Collapse any active selection before loading new text to avoid stale cursor state.
        convo.ClearSelection();
        convo.LoadText(lines);
        convo.MoveEnd();
    }
    
    private List<List<Cell>> BuildConvoSnapshot()
    {
        var lineCount = conversationLines.Count;
        while (lineCount > 1 && conversationLines[lineCount - 1].Count == 0)
            lineCount--;

        int width = convo.Frame.Width > 0 ? convo.Frame.Width : TuiSessionContext.App.Driver?.Cols ?? 80;

        // Account for potential vertical scrollbar which consumes 1 column on the right
        if (convo.CanScrollVertical())
            width = Math.Max(1, width - 1);

        var snapshot = new List<List<Cell>>();
        for (var i = 0; i < lineCount; i++)
        {
            if (noWrapLineIndices.Contains(i))
                snapshot.Add([.. conversationLines[i]]);
            else
                snapshot.AddRange(WrapCellLine(conversationLines[i], width));
        }

        if (snapshot.Count == 0) snapshot.Add([]);

        lock (streamCursorLock)
        {
            if (streamCursorVisible)
                snapshot[^1].Add(new Cell(Palette.Bright, false, "\u258c"));
        }

        return snapshot;
    }
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
    private static List<List<Cell>> WrapCellLine(List<Cell> cells, int width)
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
                // Spaces (1 column each) fill remaining room on a non-empty line; skip at line start.
                if (curCols > 0)
                    for (int j = tokenStart; j < i && curCols < width; j++) { current.Add(Copy(cells[j])); curCols++; }
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
                    while (current.Count > 0 && CellRune(current[^1]).Value == ' ') current.RemoveAt(current.Count - 1);
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
