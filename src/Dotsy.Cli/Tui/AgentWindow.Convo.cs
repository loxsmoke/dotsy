using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using Terminal.Gui;
using TGAttribute = Terminal.Gui.Attribute;

namespace Dotsy.Cli.Tui;

public partial class AgentWindow
{
    // ══ Public thread-safe conversation API ═══════════════════════════════════

    public void WriteConvo(string text) =>
        Application.Invoke(() => AppendConvo(text, Palette.Normal));

    public void WriteConvoError(string text) =>
        Application.Invoke(() => AppendConvo(text, Palette.Err));

    public void WriteConvoBullet(string text) =>
        Application.Invoke(() =>
        {
            AppendConvo("• ", Palette.Bullet);
            AppendConvo(text + "\n", Palette.Bright);
        });

    public void WriteConvoSubtask(string text) =>
        Application.Invoke(() =>
        {
            AppendConvo("  · ", Palette.Sub);
            AppendConvo(text + "\n", Palette.Normal);
        });

    // File change summary line: "  path  +N  -N"
    private static readonly TGAttribute FileAdd = new(ColorName16.Green, ColorName16.Black);
    private static readonly TGAttribute FileDel = new(ColorName16.Red, ColorName16.Black);

    public void WriteConvoFileChange(string path, int added, int deleted) =>
        Application.Invoke(() =>
        {
            AppendConvo($"  {path}", Palette.Normal);
            AppendConvo($"  +{added}", FileAdd);
            AppendConvo($"  -{deleted}\n", FileDel);
        });

    public void WriteConvoDiffHdr(string text) =>
    Application.Invoke(() => AppendConvo(text, Palette.DiffHdr));

    public void WriteConvoDiffAdd(int lineNum, string text) =>
        Application.Invoke(() => AddDiffLine(lineNum, '+', text,
            new TGAttribute(ColorName16.BrightGreen, ColorName16.Green),
            new TGAttribute(ColorName16.BrightGreen, ColorName16.Green)));

    public void WriteConvoDiffDel(int lineNum, string text) =>
        Application.Invoke(() => AddDiffLine(lineNum, '-', text,
            new TGAttribute(ColorName16.BrightRed, ColorName16.Red),
            new TGAttribute(ColorName16.BrightRed, ColorName16.Red)));

    public void WriteConvoDiffCtx(int lineNum, string text) =>
        Application.Invoke(() => AddDiffLine(lineNum, ' ', text,
            new TGAttribute(ColorName16.DarkGray, ColorName16.Black),
            Palette.DiffCtx));

    // Full-width diff line: indent + line-num + indicator + content + background padding
    private void AddDiffLine(int lineNum, char indicator, string content,
        TGAttribute numAttr, TGAttribute lineAttr)
    {
        const int PadWidth = 160;
        const int Indent = 2;

        if (conversationLines[^1].Count > 0) conversationLines.Add([]);
        var line = conversationLines[^1];

        Cell C(char ch, TGAttribute a) => new(a, false, new System.Text.Rune(ch));

        // Indent — coloured for add/del, normal for context
        var indentAttr = indicator == ' ' ? Palette.Normal : lineAttr;
        for (var i = 0; i < Indent; i++) line.Add(C(' ', indentAttr));

        // Line number: 4 chars right-aligned
        foreach (var ch in lineNum.ToString().PadLeft(4))
            line.Add(C(ch, numAttr));

        // Space + indicator + space
        line.Add(C(' ', lineAttr));
        line.Add(C(indicator, lineAttr));
        line.Add(C(' ', lineAttr));

        // Content (rune-aware; TextToCells strips CR/LF)
        line.AddRange(TextToCells(content, lineAttr));

        // Pad add/del lines to fill the terminal width (capped so they don't word-wrap)
        if (indicator != ' ')
        {
            int padTo = Math.Min(PadWidth, convoWrapWidth > 0 ? convoWrapWidth : PadWidth);
            while (line.Count < padTo)
                line.Add(C(' ', lineAttr));
        }

        // Mark this line as no-wrap so it displays correctly even if wider than view
        int diffLineIdx = conversationLines.Count - 1;
        noWrapLineIndices.Add(diffLineIdx);

        conversationLines.Add([]);
        ReloadConvo();
    }

    // ══ Private helpers ═══════════════════════════════════════════════════════

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
            if (rune.Value == '\r' || rune.GetColumns() <= 0) continue; // drop CR + zero-width (avoids grid desync)
            conversationLines[^1].Add(new Cell(attr, false, Glyphs.Safe(rune)));
        }
        ReloadConvo();
    }

    private void ReloadConvo()
    {
        var lines = BuildConvoSnapshot();
        // Both resets guard against the same Terminal.Gui bug: TextView.Load calls
        // _historyText.Clear() *before* ResetPosition(), so OnContentsChanged() fires
        // with stale (post-click) cursor and selection against the freshly loaded model.
        //   IsSelecting=false  → prevents ProcessAutocomplete→SelectedText→GetRegion
        //                        crashing with out-of-bounds GetRange (line 4662).
        //   CursorPosition=(0,0) → prevents ProcessInheritsPreviousColorScheme
        //                          crashing with stale CurrentColumn (line 5777).
        convo.IsSelecting = false;
        convo.CursorPosition = new Point(0, 0);
        convo.Load(lines);
        convo.MoveEnd();
    }
    
    private List<List<Cell>> BuildConvoSnapshot()
    {
        var lineCount = conversationLines.Count;
        while (lineCount > 1 && conversationLines[lineCount - 1].Count == 0)
            lineCount--;

        int width = convo.Frame.Width > 0 ? convo.Frame.Width : Application.Driver?.Cols ?? 80;

        // Account for potential vertical scrollbar which consumes 1 column on the right
        if (convo.ShowScrollBars && convo.CanScrollVertical())
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
                snapshot[^1].Add(new Cell(Palette.Bright, false, new System.Text.Rune('▌')));
        }

        return snapshot;
    }
    // Display columns a cell occupies, per Terminal.Gui's own renderer (which advances the cursor
    // by Rune.GetColumns()). Wrapping uses this so wrapped lines match what TG actually draws. Emoji
    // are replaced with 1-column glyphs at cell creation (Glyphs.Safe), so every cell here has a
    // width TG and the terminal agree on; clamp to >= 1 as a guard.
    private static int CellColumns(Cell cell) => Math.Max(1, cell.Rune.GetColumns());

    // Word-wraps a single logical line (list of colored cells) into display lines at `width`
    // DISPLAY COLUMNS (not cell count). Preserves per-cell attributes exactly — no color bleed.
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

        static Cell Copy(Cell c) => new(c.Attribute, false, c.Rune);

        int i = 0;
        while (i < cells.Count)
        {
            bool isSpace = cells[i].Rune.Value == ' ';
            int tokenStart = i;
            while (i < cells.Count && (cells[i].Rune.Value == ' ') == isSpace) i++;

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
                    while (current.Count > 0 && current[^1].Rune.Value == ' ') current.RemoveAt(current.Count - 1);
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

        while (current.Count > 0 && current[^1].Rune.Value == ' ')
            current.RemoveAt(current.Count - 1);
        result.Add(current);

        return result;
    }
}
