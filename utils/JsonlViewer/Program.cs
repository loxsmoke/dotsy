using System.Text;
using System.Text.Json;
using System.Drawing;
using TGAttribute = Terminal.Gui.Drawing.Attribute;

if (args.Length != 1 || args[0] is "-h" or "--help" or "/?")
{
    Console.Error.WriteLine("Usage: jsonl-viewer <file.jsonl>");
    return args.Length == 1 ? 0 : 2;
}

var path = Path.GetFullPath(args[0]);
if (!File.Exists(path))
{
    Console.Error.WriteLine($"File not found: {path}");
    return 1;
}

List<SourceLine> lines;
try
{
    lines = JsonlFormatter.Load(path);
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

using var app = Application.Create().Init();
{
    var window = new Window
    {
        Title = Path.GetFileName(path),
        X = 0,
        Y = 0,
        Width = Dim.Fill(),
        Height = Dim.Fill()
    };

    var status = new Label
    {
        Text = "Arrows/PageUp/PageDown move  |  Shift+arrows selects  |  Ctrl+C copies  |  Esc quits",
        X = 1,
        Y = Pos.AnchorEnd(1),
        Width = Dim.Fill(2),
        Height = 1
    };

    var viewer = new JsonlView(lines, status, app)
    {
        X = 0,
        Y = 0,
        Width = Dim.Fill(),
        Height = Dim.Fill(1)
    };

    window.Add(viewer, status);
    viewer.SetFocus();
    app.Run(window);
}

return 0;

internal sealed record Segment(string Text, TGAttribute Attribute);

internal sealed record SourceLine(IReadOnlyList<Segment> Segments)
{
    public string Text { get; } = string.Concat(Segments.Select(s => s.Text));
}

internal static class JsonlFormatter
{
    private static readonly JsonSerializerOptions PrettyOptions = new() { WriteIndented = true };

    public static List<SourceLine> Load(string path)
    {
        var result = new List<SourceLine>();
        var recordNumber = 1;

        foreach (var rawLine in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                result.Add(Plain(""));
                recordNumber++;
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(rawLine);
                var pretty = JsonSerializer.Serialize(document.RootElement, PrettyOptions);
                result.Add(Plain($"// record {recordNumber}"));
                foreach (var line in pretty.Split('\n'))
                    result.Add(new SourceLine(JsonSyntax.Highlight(line.TrimEnd('\r'))));
            }
            catch (JsonException ex)
            {
                result.Add(Plain($"// record {recordNumber}: invalid JSONL ({ex.Message})", Palette.Error));
                result.Add(new SourceLine(JsonSyntax.Highlight(rawLine)));
            }

            recordNumber++;
        }

        if (result.Count == 0)
            result.Add(Plain("// empty file", Palette.Dim));

        return result;
    }

    private static SourceLine Plain(string text, TGAttribute? attribute = null) =>
        new([new Segment(text, attribute ?? Palette.Normal)]);
}

internal static class JsonSyntax
{
    public static IReadOnlyList<Segment> Highlight(string line)
    {
        var segments = new List<Segment>();
        var i = 0;

        while (i < line.Length)
        {
            var c = line[i];

            if (c == '"')
            {
                var end = i + 1;
                while (end < line.Length)
                {
                    if (line[end] == '\\' && end + 1 < line.Length)
                    {
                        end += 2;
                        continue;
                    }

                    if (line[end] == '"')
                    {
                        end++;
                        break;
                    }

                    end++;
                }

                var peek = end;
                while (peek < line.Length && char.IsWhiteSpace(line[peek]))
                    peek++;

                var isKey = peek < line.Length && line[peek] == ':';
                segments.Add(new Segment(line[i..end], isKey ? Palette.Key : Palette.String));
                i = end;
                continue;
            }

            if (char.IsAsciiDigit(c) || (c == '-' && i + 1 < line.Length && char.IsAsciiDigit(line[i + 1])))
            {
                var end = i + 1;
                while (end < line.Length && (char.IsAsciiDigit(line[end]) || line[end] is '.' or 'e' or 'E' or '+' or '-'))
                    end++;

                segments.Add(new Segment(line[i..end], Palette.Number));
                i = end;
                continue;
            }

            if (char.IsAsciiLetter(c))
            {
                var end = i + 1;
                while (end < line.Length && char.IsAsciiLetter(line[end]))
                    end++;

                var word = line[i..end];
                segments.Add(new Segment(word, word is "true" or "false" or "null" ? Palette.Keyword : Palette.Normal));
                i = end;
                continue;
            }

            segments.Add(new Segment(c.ToString(), "{}[]:,".Contains(c) ? Palette.Punctuation : Palette.Normal));
            i++;
        }

        return segments;
    }
}

internal sealed class JsonlView : View
{
    private readonly IReadOnlyList<SourceLine> _sourceLines;
    private readonly Label _status;
    private readonly IApplication _app;
    private readonly List<DisplayLine> _displayLines = [];
    private int _cursorRow;
    private int _cursorColumn;
    private int _topRow;
    private Position? _selectionAnchor;
    private string? _statusOverride;

    public JsonlView(IReadOnlyList<SourceLine> sourceLines, Label status, IApplication app)
    {
        _sourceLines = sourceLines;
        _status = status;
        _app = app;
        CanFocus = true;
        SetScheme(new Scheme { Normal = Palette.Normal, Focus = Palette.Normal, HotNormal = Palette.Key, HotFocus = Palette.Key });
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        Reflow();
        SetAttribute(Palette.Normal);

        var height = Math.Max(0, Viewport.Height);
        var width = Math.Max(1, Viewport.Width);
        for (var y = 0; y < height; y++)
        {
            Move(0, y);
            RenderRow(_topRow + y, width);
        }

        UpdateStatus();
        return true;
    }

    public Point? PositionCursor()
    {
        Reflow();
        EnsureCursorVisible();
        var row = Math.Clamp(_cursorRow - _topRow, 0, Math.Max(0, Viewport.Height - 1));
        var col = Math.Clamp(_cursorColumn, 0, Math.Max(0, Viewport.Width - 1));
        Move(col, row);
        return new Point(col, row);
    }

    protected override bool OnKeyDown(Key key)
    {
        Reflow();

        if (key == Key.Esc || key == Key.Q || key == Key.Q.WithCtrl)
        {
            _app.RequestStop();
            return true;
        }

        if (key == Key.C.WithCtrl)
        {
            CopySelection();
            SetNeedsDraw();
            return true;
        }

        var selecting = key == Key.CursorUp.WithShift ||
            key == Key.CursorDown.WithShift ||
            key == Key.CursorLeft.WithShift ||
            key == Key.CursorRight.WithShift ||
            key == Key.PageUp.WithShift ||
            key == Key.PageDown.WithShift ||
            key == Key.Home.WithShift ||
            key == Key.End.WithShift;

        if (selecting && _selectionAnchor is null)
            _selectionAnchor = CurrentPosition();
        else if (!selecting && key.KeyCode is not KeyCode.Null)
            _selectionAnchor = null;

        var page = Math.Max(1, Viewport.Height - 1);
        var handled = true;

        switch (key.NoShift.KeyCode)
        {
            case KeyCode.CursorUp:
                MoveCursorRows(-1);
                break;
            case KeyCode.CursorDown:
                MoveCursorRows(1);
                break;
            case KeyCode.CursorLeft:
                MoveCursorColumns(-1);
                break;
            case KeyCode.CursorRight:
                MoveCursorColumns(1);
                break;
            case KeyCode.PageUp:
                MoveCursorRows(-page);
                break;
            case KeyCode.PageDown:
                MoveCursorRows(page);
                break;
            case KeyCode.Home:
                _cursorColumn = 0;
                break;
            case KeyCode.End:
                _cursorColumn = CurrentLineLength();
                break;
            default:
                handled = false;
                break;
        }

        if (!handled)
            return false;

        _statusOverride = null;
        EnsureCursorVisible();
        SetNeedsDraw();
        return true;
    }

    private void Reflow()
    {
        var width = Math.Max(1, Viewport.Width);
        if (_displayLines.Count > 0 && _displayLines[0].Width == width)
            return;

        _displayLines.Clear();
        for (var sourceIndex = 0; sourceIndex < _sourceLines.Count; sourceIndex++)
        {
            var sourceLine = _sourceLines[sourceIndex];
            if (sourceLine.Text.Length == 0)
            {
                _displayLines.Add(new DisplayLine(width, sourceIndex, 0, 0, []));
                continue;
            }

            var offset = 0;
            while (offset < sourceLine.Text.Length)
            {
                var length = GetWrapLength(sourceLine.Text, offset, width);
                _displayLines.Add(new DisplayLine(width, sourceIndex, offset, length, Slice(sourceLine.Segments, offset, length)));
                offset += length;
            }
        }

        _cursorRow = Math.Clamp(_cursorRow, 0, Math.Max(0, _displayLines.Count - 1));
        _topRow = Math.Clamp(_topRow, 0, MaxTopRow());
    }

    private static int GetWrapLength(string text, int offset, int width)
    {
        var remaining = text.Length - offset;
        if (remaining <= width)
            return remaining;

        var end = offset + width;
        for (var i = end - 1; i > offset; i--)
        {
            if (char.IsWhiteSpace(text[i]))
                return i - offset + 1;
        }

        return width;
    }

    private static IReadOnlyList<Segment> Slice(IReadOnlyList<Segment> segments, int offset, int length)
    {
        var result = new List<Segment>();
        var consumed = 0;
        var remaining = length;

        foreach (var segment in segments)
        {
            var next = consumed + segment.Text.Length;
            if (next <= offset)
            {
                consumed = next;
                continue;
            }

            var start = Math.Max(0, offset - consumed);
            var take = Math.Min(segment.Text.Length - start, remaining);
            if (take > 0)
            {
                result.Add(new Segment(segment.Text.Substring(start, take), segment.Attribute));
                remaining -= take;
            }

            if (remaining == 0)
                break;

            consumed = next;
        }

        return result;
    }

    private void RenderRow(int displayRow, int width)
    {
        if (displayRow >= _displayLines.Count)
        {
            AddStr(new string(' ', width));
            return;
        }

        var line = _displayLines[displayRow];
        var x = 0;
        foreach (var segment in line.Segments)
        {
            foreach (var ch in segment.Text)
            {
                SetAttribute(CellAttribute(displayRow, x, segment.Attribute));
                AddRune(ch);
                x++;
            }
        }

        while (x < width)
        {
            SetAttribute(CellAttribute(displayRow, x, Palette.Normal));
            AddRune(' ');
            x++;
        }
    }

    private TGAttribute CellAttribute(int row, int column, TGAttribute fallback)
    {
        if (row == _cursorRow && column == _cursorColumn)
            return Palette.Cursor;

        return IsSelected(row, column) ? Palette.Selection : fallback;
    }

    private bool IsSelected(int row, int column)
    {
        if (_selectionAnchor is null)
            return false;

        var a = _selectionAnchor.Value;
        var b = CurrentPosition();
        if (b.CompareTo(a) < 0)
            (a, b) = (b, a);

        var here = new Position(row, column);
        return here.CompareTo(a) >= 0 && here.CompareTo(b) < 0;
    }

    private void MoveCursorRows(int delta)
    {
        _cursorRow = Math.Clamp(_cursorRow + delta, 0, Math.Max(0, _displayLines.Count - 1));
        _cursorColumn = Math.Clamp(_cursorColumn, 0, CurrentLineLength());
    }

    private void MoveCursorColumns(int delta)
    {
        _cursorColumn += delta;
        while (_cursorColumn < 0 && _cursorRow > 0)
        {
            _cursorRow--;
            _cursorColumn += CurrentLineLength() + 1;
        }

        while (_cursorColumn > CurrentLineLength() && _cursorRow < _displayLines.Count - 1)
        {
            _cursorColumn -= CurrentLineLength() + 1;
            _cursorRow++;
        }

        _cursorColumn = Math.Clamp(_cursorColumn, 0, CurrentLineLength());
    }

    private int CurrentLineLength() =>
        _displayLines.Count == 0 ? 0 : _displayLines[_cursorRow].Length;

    private void EnsureCursorVisible()
    {
        var height = Math.Max(1, Viewport.Height);
        if (_cursorRow < _topRow)
            _topRow = _cursorRow;
        else if (_cursorRow >= _topRow + height)
            _topRow = _cursorRow - height + 1;

        _topRow = Math.Clamp(_topRow, 0, MaxTopRow());
    }

    private int MaxTopRow() => Math.Max(0, _displayLines.Count - Math.Max(1, Viewport.Height));

    private Position CurrentPosition() => new(_cursorRow, _cursorColumn);

    private void CopySelection()
    {
        var text = SelectedText();
        if (string.IsNullOrEmpty(text))
        {
            _status.Text = "No selection to copy";
            _statusOverride = _status.Text?.ToString();
            return;
        }

        _status.Text = _app.Clipboard?.TrySetClipboardData(text) == true
            ? $"Copied {text.Length:n0} characters"
            : "Clipboard is not available in this terminal";
        _statusOverride = _status.Text?.ToString();
    }

    private string SelectedText()
    {
        if (_selectionAnchor is null)
            return "";

        var start = _selectionAnchor.Value;
        var end = CurrentPosition();
        if (end.CompareTo(start) < 0)
            (start, end) = (end, start);

        if (start.CompareTo(end) == 0)
            return "";

        var builder = new StringBuilder();
        for (var row = start.Row; row <= end.Row; row++)
        {
            var line = _displayLines[row];
            var from = row == start.Row ? start.Column : 0;
            var to = row == end.Row ? end.Column : line.Length;
            from = Math.Clamp(from, 0, line.Length);
            to = Math.Clamp(to, 0, line.Length);

            if (to > from)
                builder.Append(_sourceLines[line.SourceLine].Text.AsSpan(line.SourceOffset + from, to - from));

            if (row != end.Row)
                builder.AppendLine();
        }

        return builder.ToString();
    }

    private void UpdateStatus()
    {
        var selection = string.IsNullOrEmpty(SelectedText()) ? "" : "  |  selection active";
        _status.Text = _statusOverride ?? $"{_cursorRow + 1:n0}/{Math.Max(1, _displayLines.Count):n0} display lines{selection}";
    }

    private sealed record DisplayLine(
        int Width,
        int SourceLine,
        int SourceOffset,
        int Length,
        IReadOnlyList<Segment> Segments);

    private readonly record struct Position(int Row, int Column) : IComparable<Position>
    {
        public int CompareTo(Position other)
        {
            var row = Row.CompareTo(other.Row);
            return row != 0 ? row : Column.CompareTo(other.Column);
        }
    }
}

internal static class Palette
{
    public static readonly TGAttribute Normal = new(ColorName16.Gray, ColorName16.Black);
    public static readonly TGAttribute Dim = new(ColorName16.DarkGray, ColorName16.Black);
    public static readonly TGAttribute Key = new(ColorName16.BrightCyan, ColorName16.Black);
    public static readonly TGAttribute String = new(ColorName16.BrightYellow, ColorName16.Black);
    public static readonly TGAttribute Number = new(ColorName16.BrightMagenta, ColorName16.Black);
    public static readonly TGAttribute Keyword = new(ColorName16.BrightGreen, ColorName16.Black);
    public static readonly TGAttribute Punctuation = new(ColorName16.White, ColorName16.Black);
    public static readonly TGAttribute Error = new(ColorName16.BrightRed, ColorName16.Black);
    public static readonly TGAttribute Selection = new(ColorName16.Black, ColorName16.BrightYellow);
    public static readonly TGAttribute Cursor = new(ColorName16.Black, ColorName16.White);
}
