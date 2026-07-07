using Dotsy.Cli.Tui.Colors;
using System.Text;

namespace Dotsy.Cli.Tui.Renderers;

/// <summary>
/// Streaming markdown renderer for the conversation view.
/// Buffers one line at a time; parses and emits per-segment colour attributes.
/// Call Write() for each incoming text chunk, Flush() when streaming ends.
/// </summary>
internal sealed class MarkdownRenderer
{
    private readonly Action<string, TGAttribute> append;
    private readonly StringBuilder line = new();
    private readonly int wrapWidth;
    private bool inCodeBlock;
    private string codeLang = "";
    private bool inBlockComment;
    // Pipe-table rows are buffered here until the block ends, then emitted aligned — a streaming
    // renderer can't size columns until it has seen every row.
    private readonly List<string> tableRows = new();

    public MarkdownRenderer(int wrapWidth, Action<string, TGAttribute> append)
    {
        this.append = append;
        this.wrapWidth = wrapWidth > 0 ? wrapWidth : 48;
    }

    public void Write(string chunk)
    {
        foreach (var ch in chunk)
        {
            if (ch == '\n') CommitLine(addNewline: true);
            else line.Append(ch);
        }
    }

    public void Flush()
    {
        if (line.Length > 0)
            CommitLine(addNewline: false);
        if (tableRows.Count > 0)
            EmitTable();
    }

    // ─────────────────────────────────────────────────────────────────────────

    private void CommitLine(bool addNewline)
    {
        RenderLine(line.ToString(), addNewline);
        line.Clear();
    }

    private void RenderLine(string raw, bool addNewline)
    {
        void End() { if (addNewline) append("\n", Palette.Normal); }

        var trimmed = raw.TrimStart();

        // Code fence toggle
        if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
        {
            if (!inCodeBlock)
            {
                codeLang = trimmed[3..].Trim().ToLowerInvariant();
                inBlockComment = false;
            }
            else
            {
                codeLang = "";
                inBlockComment = false;
            }
            inCodeBlock = !inCodeBlock;
            End();
            return;
        }

        if (inCodeBlock)
        {
            append("  ", Palette.Normal);
            inBlockComment = SyntaxHighlighter.Highlight(codeLang, raw, inBlockComment, append);
            End();
            return;
        }

        // Pipe-table row: buffer it; the whole block is emitted, aligned, when a non-table line
        // (or Flush) closes it. Deferring is why column widths can be computed at all.
        if (trimmed.StartsWith('|'))
        {
            tableRows.Add(raw);
            return;
        }
        if (tableRows.Count > 0)
            EmitTable();

        // Blank line
        if (trimmed.Length == 0) { End(); return; }

        // Headings (any level — strip all # and leading space)
        if (trimmed[0] == '#')
        {
            var body = trimmed.TrimStart('#').TrimStart();
            if (body.Length > 0)
            {
                RenderInline(body, Palette.Bright);
                End();
                return;
            }
        }

        // Horizontal rule
        if (trimmed is "---" or "***" or "___" or "===")
        {
            append(new string('─', wrapWidth), Palette.Dim);
            End();
            return;
        }

        // Blockquote
        if (trimmed.StartsWith("> "))
        {
            append("│ ", Palette.Dim);
            RenderInline(trimmed[2..], Palette.Dim);
            End();
            return;
        }

        // Indented code block (4 spaces or tab)
        if (raw.StartsWith("    ") || raw.StartsWith("\t"))
        {
            append("  " + trimmed, Palette.Code);
            End();
            return;
        }

        // Unordered bullet
        if (trimmed.Length > 2 &&
            (trimmed.StartsWith("- ") || trimmed.StartsWith("* ") || trimmed.StartsWith("+ ")))
        {
            append("  • ", Palette.Bullet);
            RenderInline(trimmed[2..], Palette.Normal);
            End();
            return;
        }

        // Numbered list: "1. " or "12. "
        {
            int dot = trimmed.IndexOf(". ");
            if (dot is > 0 and <= 3 && trimmed[..dot].All(char.IsAsciiDigit))
            {
                append("  " + trimmed[..(dot + 2)], Palette.Bullet);
                RenderInline(trimmed[(dot + 2)..], Palette.Normal);
                End();
                return;
            }
        }

        RenderInline(raw, Palette.Normal);
        End();
    }

    private void RenderInline(string text, TGAttribute baseAttr)
    {
        var buf = new StringBuilder();
        int i = 0;

        void Flush()
        {
            if (buf.Length == 0) return;
            append(buf.ToString(), baseAttr);
            buf.Clear();
        }

        while (i < text.Length)
        {
            char c = text[i];

            // Link: [label](url) — show label only
            if (c == '[')
            {
                int cb = text.IndexOf(']', i + 1);
                if (cb > 0 && cb + 1 < text.Length && text[cb + 1] == '(')
                {
                    int cp = text.IndexOf(')', cb + 2);
                    if (cp > 0)
                    {
                        Flush();
                        append(text[(i + 1)..cb], Palette.Cmd);
                        i = cp + 1;
                        continue;
                    }
                }
            }

            // Inline code: `text`
            if (c == '`')
            {
                int end = text.IndexOf('`', i + 1);
                if (end > i)
                {
                    Flush();
                    append(text[(i + 1)..end], Palette.Code);
                    i = end + 1;
                    continue;
                }
            }

            // Strikethrough: ~~text~~
            if (c == '~' && i + 1 < text.Length && text[i + 1] == '~')
            {
                int end = text.IndexOf("~~", i + 2, StringComparison.Ordinal);
                if (end >= 0)
                {
                    Flush();
                    append(text[(i + 2)..end], Palette.Dim);
                    i = end + 2;
                    continue;
                }
            }

            // Bold: **text** or __text__
            if ((c == '*' || c == '_') && i + 1 < text.Length && text[i + 1] == c)
            {
                var marker = new string(c, 2);
                int end = text.IndexOf(marker, i + 2, StringComparison.Ordinal);
                if (end >= 0)
                {
                    Flush();
                    append(text[(i + 2)..end], Palette.Bright);
                    i = end + 2;
                    continue;
                }
            }

            // Italic: *text* only (skip _text_ to avoid false positives on snake_case)
            if (c == '*')
            {
                int end = text.IndexOf('*', i + 1);
                if (end > i + 1)
                {
                    Flush();
                    append(text[(i + 1)..end], Palette.Bright);
                    i = end + 1;
                    continue;
                }
            }

            buf.Append(c);
            i++;
        }

        Flush();
    }

    // Emits the buffered pipe-table as aligned columns: header row (before the `---` separator) in
    // bright, a divider, then body rows, cells padded to each column's widest value and joined by │.
    private void EmitTable()
    {
        if (tableRows.Count == 0)
            return;

        var rows = tableRows.Select(SplitRow).ToList();
        tableRows.Clear();

        int sep = rows.FindIndex(cells => cells.Length > 0 && cells.All(IsSeparatorCell));
        int cols = rows.Max(cells => cells.Length);
        var width = new int[cols];
        for (int ri = 0; ri < rows.Count; ri++)
        {
            if (ri == sep) continue;
            for (int ci = 0; ci < rows[ri].Length; ci++)
                width[ci] = Math.Max(width[ci], rows[ri][ci].Length);
        }

        for (int ri = 0; ri < rows.Count; ri++)
        {
            if (ri == sep)
            {
                append(string.Join("─┼─", width.Select(w => new string('─', w))), Palette.Dim);
                append("\n", Palette.Normal);
                continue;
            }

            var attr = sep >= 0 && ri < sep ? Palette.Bright : Palette.Normal;
            var cells = rows[ri];
            var sb = new StringBuilder();
            for (int ci = 0; ci < cols; ci++)
            {
                sb.Append((ci < cells.Length ? cells[ci] : "").PadRight(width[ci]));
                if (ci < cols - 1)
                    sb.Append(" │ ");
            }
            append(sb.ToString(), attr);
            append("\n", Palette.Normal);
        }
    }

    // Splits "| a | b |" into ["a","b"], dropping the outer pipes and stripping inline markup so
    // column widths measure visible text.
    private static string[] SplitRow(string row)
    {
        var t = row.Trim();
        if (t.StartsWith('|')) t = t[1..];
        if (t.EndsWith('|')) t = t[..^1];
        return t.Split('|').Select(cell => StripInline(cell.Trim())).ToArray();
    }

    private static bool IsSeparatorCell(string cell) =>
        cell.Length > 0 && cell.All(ch => ch is '-' or ':' or ' ') && cell.Contains('-');

    // Removes inline markdown markers so cell text measures/renders as its visible form (mirrors
    // RenderInline: link -> label, drop `code`, **bold**, __bold__, ~~strike~~, *italics*).
    private static string StripInline(string s)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '[')
            {
                int cb = s.IndexOf(']', i + 1);
                if (cb > 0 && cb + 1 < s.Length && s[cb + 1] == '(')
                {
                    int cp = s.IndexOf(')', cb + 2);
                    if (cp > 0) { sb.Append(s[(i + 1)..cb]); i = cp + 1; continue; }
                }
            }
            if (c == '`') { i++; continue; }
            if (c == '~' && i + 1 < s.Length && s[i + 1] == '~') { i += 2; continue; }
            if ((c == '*' || c == '_') && i + 1 < s.Length && s[i + 1] == c) { i += 2; continue; }
            if (c == '*') { i++; continue; }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }
}
