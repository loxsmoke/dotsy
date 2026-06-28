using System.Text;
using System.Drawing;
using TGAttribute = Terminal.Gui.Drawing.Attribute;

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
