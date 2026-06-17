using System.Text;
using Terminal.Gui;
using TGAttribute = Terminal.Gui.Attribute;

namespace Dotsy.Cli.Tui;

/// <summary>
/// Streaming markdown renderer for the conversation view.
/// Buffers one line at a time; parses and emits per-segment colour attributes.
/// Call Write() for each incoming text chunk, Flush() when streaming ends.
/// </summary>
internal sealed class MarkdownRenderer
{
    private readonly Action<string, TGAttribute> _append;
    private readonly StringBuilder _line = new();
    private readonly int _wrapWidth;
    private bool _inCodeBlock;
    private string _codeLang = "";
    private bool _inBlockComment;

    public MarkdownRenderer(int wrapWidth, Action<string, TGAttribute> append)
    {
        _append = append;
        _wrapWidth = wrapWidth > 0 ? wrapWidth : 48;
    }

    public void Write(string chunk)
    {
        foreach (var ch in chunk)
        {
            if (ch == '\n') CommitLine(addNewline: true);
            else _line.Append(ch);
        }
    }

    public void Flush()
    {
        if (_line.Length > 0)
            CommitLine(addNewline: false);
    }

    // ─────────────────────────────────────────────────────────────────────────

    private void CommitLine(bool addNewline)
    {
        RenderLine(_line.ToString(), addNewline);
        _line.Clear();
    }

    private void RenderLine(string raw, bool addNewline)
    {
        void End() { if (addNewline) _append("\n", Palette.Normal); }

        var trimmed = raw.TrimStart();

        // Code fence toggle
        if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
        {
            if (!_inCodeBlock)
            {
                _codeLang = trimmed[3..].Trim().ToLowerInvariant();
                _inBlockComment = false;
            }
            else
            {
                _codeLang = "";
                _inBlockComment = false;
            }
            _inCodeBlock = !_inCodeBlock;
            End();
            return;
        }

        if (_inCodeBlock)
        {
            _append("  ", Palette.Normal);
            _inBlockComment = SyntaxHighlighter.Highlight(_codeLang, raw, _inBlockComment, _append);
            End();
            return;
        }

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
            _append(new string('─', _wrapWidth), Palette.Dim);
            End();
            return;
        }

        // Blockquote
        if (trimmed.StartsWith("> "))
        {
            _append("│ ", Palette.Dim);
            RenderInline(trimmed[2..], Palette.Dim);
            End();
            return;
        }

        // Indented code block (4 spaces or tab)
        if (raw.StartsWith("    ") || raw.StartsWith("\t"))
        {
            _append("  " + trimmed, Palette.Code);
            End();
            return;
        }

        // Unordered bullet
        if (trimmed.Length > 2 &&
            (trimmed.StartsWith("- ") || trimmed.StartsWith("* ") || trimmed.StartsWith("+ ")))
        {
            _append("  • ", Palette.Bullet);
            RenderInline(trimmed[2..], Palette.Normal);
            End();
            return;
        }

        // Numbered list: "1. " or "12. "
        {
            int dot = trimmed.IndexOf(". ");
            if (dot is > 0 and <= 3 && trimmed[..dot].All(char.IsAsciiDigit))
            {
                _append("  " + trimmed[..(dot + 2)], Palette.Bullet);
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
            _append(buf.ToString(), baseAttr);
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
                        _append(text[(i + 1)..cb], Palette.Cmd);
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
                    _append(text[(i + 1)..end], Palette.Code);
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
                    _append(text[(i + 2)..end], Palette.Dim);
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
                    _append(text[(i + 2)..end], Palette.Bright);
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
                    _append(text[(i + 1)..end], Palette.Bright);
                    i = end + 1;
                    continue;
                }
            }

            buf.Append(c);
            i++;
        }

        Flush();
    }
}
