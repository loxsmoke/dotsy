using Dotsy.Cli.Tui.Colors;
using System.Reflection;
using System.Text;
using Terminal.Gui.Editor;

namespace Dotsy.Cli.Tui;

// Editable multi-line input that submits on Enter and navigates command history with Up/Down.
// Printable characters are inserted synchronously so terminal echo stays responsive.
//
// Key design notes for Terminal.Gui v2:
//   - OnKeyDown override fires before InvokeCommands; return true to stop further processing.
//   - key.KeyCode for modifier combos includes the mask bits (e.g. Ctrl+Q is
//     KeyCode.Q | KeyCode.CtrlMask). Use Key == comparison, not key.IsCtrl && key.KeyCode == X.
//   - CursorUp/Down use boundary detection: navigate history from the first/last line,
//     fall through to normal cursor movement for interior lines.
internal sealed class MultilineInput : Editor
{
    public event EventHandler<string>? Submitted;
    public event EventHandler?         HistoryPrev;
    public event EventHandler?         HistoryNext;
    public event EventHandler?         QuitRequested;    // Ctrl+Q
    public event EventHandler?         CancelRequested;  // Ctrl+G

    public Func<Key, bool>? KeyInterceptor { get; set; }
    public Func<string?>? HintProvider { get; set; }

    // Pre-built Key objects for modifier combos; avoids allocating on every keypress.
    private static readonly Key CtrlQ = new Key(KeyCode.Q).WithCtrl;
    private static readonly Key CtrlC = new Key(KeyCode.C).WithCtrl;
    private static readonly Key CtrlG = new Key(KeyCode.G).WithCtrl;
    private static readonly Key CtrlInsert = Key.InsertChar.WithCtrl;
    private static readonly MethodInfo? ExtendCaretByMethod =
        typeof(Editor).GetMethod("ExtendCaretBy", BindingFlags.Instance | BindingFlags.NonPublic);
    private readonly Action<int>? extendCaretBy;

    // Fired when content or caret position changes (mirrors TextView.ContentsChanged behaviour).
    public event EventHandler<EventArgs>? ContentsChanged;

    public MultilineInput()
    {
        CanFocus = true;
        WordWrap = true;
        TabStop = TabBehavior.TabStop;
        SetScheme(Palette.InputScheme());

        // The Editor base binds Tab/Shift+Tab to insert-tab commands (there's no TabKeyAddsTab
        // toggle like TextView had). Drop those bindings so Tab is left unhandled and bubbles to
        // AgentWindow, which uses it to switch focus between panels instead of inserting a tab.
        KeyBindings.Remove(Key.Tab);
        KeyBindings.Remove(Key.Tab.WithShift);

        extendCaretBy = CreateExtendCaretByDelegate(this);

        HasFocusChanged += OnHasFocusChanged;
        ContentChanged  += (_, _) => ContentsChanged?.Invoke(this, EventArgs.Empty);
        CaretChanged    += (_, _) => ContentsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnHasFocusChanged(object? sender, EventArgs e)
    {
        if (HasFocus)
        {
            SetNeedsDraw();
        }
    }

    public void PositionCursor() => SetNeedsDraw();

    public void MoveEnd()
    {
        CaretOffset = Document?.TextLength ?? 0;
        ScrollToBottom();
        MarkInputDirty();
    }

    /// <summary>
    /// Scrolls the view so the last visible row shows the bottom of the content.
    /// </summary>
    public void ScrollToBottom()
    {
        // Editor is a Terminal.Gui View; it keeps its content size in sync with the wrapped
        // line count, so scroll the Viewport to the bottom using that rather than recomputing
        // the wrap map ourselves.
        int totalRows   = GetContentSize().Height;
        int visibleRows = Math.Max(1, Viewport.Height);
        int targetY     = Math.Max(0, totalRows - visibleRows);

        if (Viewport.Y != targetY)
            Viewport = Viewport with { Y = targetY };
    }

    /// <summary>
    /// Appends text at the caret position (used during paste).
    /// </summary>
    public void InsertText(string text)
    {
        text = Glyphs.Sanitize(text);
        Document?.Insert(CaretOffset, text);
        CaretOffset += text.Length;
        MarkInputDirty();
    }

    /// <summary>
    /// Replaces all content and positions the caret at the end.
    /// Does NOT scroll TopRow; callers that need scrolling should call MoveEnd or ScrollToBottom separately.
    /// </summary>
    public void SetTextAndMoveEnd(string text)
    {
        Document?.Replace(0, Document.TextLength, text);
        CaretOffset = Document?.TextLength ?? 0;
        MarkInputDirty();
    }

    protected override bool OnKeyDown(Key key)
    {
        if (KeyInterceptor?.Invoke(key) == true)
        {
            key.Handled = true;
            return true;
        }

        if (TryHandleFastSelectionKey(key))
        {
            key.Handled = true;
            return true;
        }

        // Shift+navigation extends the selection. Invoke the matching TextView "extend" command
        // directly instead of relying on the bound key being matched; selection then works even
        // where the shift+arrow keybinding lookup misbehaves.
        if (TextViewInteractions.TryGetShiftSelectionCommand(key, out var selectionCommand))
        {
            InvokeCommand(selectionCommand);
            key.Handled = true;
            return true;
        }

        // Plain navigation collapses any active selection (from Shift, Ctrl+A, or the mouse).
        // TextView only auto-clears selections it started itself via Shift, so clear it here for
        // selections made by SelectAll or the mouse before letting the cursor move normally.
        if (HasSelection && IsPlainNavigationKey(key))
        {
            ClearSelection();
            MarkInputDirty();
        }

        // Enter: submit.
        if (key.KeyCode == KeyCode.Enter)
        {
            var text = Text?.ToString().Trim() ?? "";
            if (!string.IsNullOrEmpty(text))
                Submitted?.Invoke(this, text);
            key.Handled = true;
            return true;
        }

        // CursorUp: navigate history from the first line; otherwise move cursor normally.
        if (key.KeyCode == KeyCode.CursorUp)
        {
            if ((Document?.GetLineByOffset(CaretOffset)?.LineNumber ?? 1) - 1 == 0)
            {
                HistoryPrev?.Invoke(this, EventArgs.Empty);
                key.Handled = true;
                return true;
            }

            MoveCaretVertical(-1);
            key.Handled = true;
            return true;
        }

        // CursorDown: navigate history from the last line; otherwise move cursor normally.
        if (key.KeyCode == KeyCode.CursorDown)
        {
            var rawText = Text?.ToString() ?? "";
            int lastRow = Math.Max(0, rawText.TrimEnd('\n', '\r').Count(c => c == '\n'));
            if ((Document?.GetLineByOffset(CaretOffset)?.LineNumber ?? 1) - 1 >= lastRow)
            {
                HistoryNext?.Invoke(this, EventArgs.Empty);
                key.Handled = true;
                return true;
            }

            MoveCaretVertical(1);
            key.Handled = true;
            return true;
        }

        // Ctrl+Q: quit.  Must use Key == (not key.IsCtrl && key.KeyCode == KeyCode.Q) because
        // TG v2 encodes modifier combos with the mask in KeyCode (Key.Q.WithCtrl binding).
        if (key == CtrlQ)
        {
            QuitRequested?.Invoke(this, EventArgs.Empty);
            key.Handled = true;
            return true;
        }

        // Ctrl+C: copy selection; no-op without a selection so it never cancels the agent.
        if (key == CtrlC)
        {
            if (HasSelection)
            {
                InvokeCommand(Command.Copy);
                key.Handled = true;
                return true;
            }

            key.Handled = true;
            return true;
        }

        // Ctrl+G: cancel running agent.
        if (key == CtrlG)
        {
            CancelRequested?.Invoke(this, EventArgs.Empty);
            key.Handled = true;
            return true;
        }

        if (key == CtrlInsert)
        {
            InvokeCommand(Command.Copy);
            key.Handled = true;
            return true;
        }

        // Tab/Shift+Tab and Esc: return false without calling base so they bubble to AgentWindow
        // (panel navigation). The insert-tab key bindings are removed in the constructor.
        if (key == Key.Tab || key == Key.Tab.WithShift || key.KeyCode == KeyCode.Esc)
            return false;

        if (TryHandleFastEditKey(key))
        {
            key.Handled = true;
            return true;
        }

        // Printable chars: insert immediately so terminal echo is tied to the keypress, not a
        // timer/dispatcher hop. The old paste coalescing path also handled ordinary typing, which
        // made every character wait for the next Terminal.Gui loop iteration before it appeared.
        if (!key.IsCtrl && !key.IsAlt && key.AsRune.Value >= 32 && !IsWideCharacter(key.AsRune))
        {
            InsertAtCaret(key.AsRune.ToString());
            key.Handled = true;
            return true;
        }

        // Everything else (Ctrl+Z, Ctrl+arrow, etc.): let TextView handle normally.
        return base.OnKeyDown(key);
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var handled = base.OnDrawingContent(context);
        DrawHint();
        return handled;
    }

    protected override bool OnMouseEvent(Mouse ev)
    {
        if (TextViewInteractions.IsContextMenuMouseEvent(ev.Flags, ContextMenu?.MouseFlags))
        {
            ev.Handled = true;
            return true;
        }

        return base.OnMouseEvent(ev);
    }

    private void InsertAtCaret(string text)
    {
        text = Glyphs.Sanitize(text);
        if (HasSelection)
            ReplaceSelection(text);
        else
            Document?.Insert(CaretOffset, text);

        MarkInputDirty();
    }

    private bool TryHandleFastSelectionKey(Key key)
    {
        if (key.IsCtrl || key.IsAlt)
            return false;

        var targetOffset = key == Key.CursorLeft.WithShift
            ? GetHorizontalOffset(-1)
            : key == Key.CursorRight.WithShift
                ? GetHorizontalOffset(1)
                : key == Key.CursorUp.WithShift
                    ? GetVerticalOffset(-1)
                    : key == Key.CursorDown.WithShift
                        ? GetVerticalOffset(1)
                        : key == Key.Home.WithShift
                            ? GetLineBoundaryOffset(moveToEnd: false)
                            : key == Key.End.WithShift
                                ? GetLineBoundaryOffset(moveToEnd: true)
                                : (int?)null;

        if (targetOffset is null)
            return false;

        ExtendSelectionTo(targetOffset.Value);
        return true;
    }

    private bool TryHandleFastEditKey(Key key)
    {
        if (key.IsCtrl || key.IsAlt)
            return false;

        if (key == Key.CursorLeft)
            return MoveCaretHorizontal(-1);

        if (key == Key.CursorRight)
            return MoveCaretHorizontal(1);

        if (key == Key.Home)
            return MoveCaretToLineBoundary(moveToEnd: false);

        if (key == Key.End)
            return MoveCaretToLineBoundary(moveToEnd: true);

        if (key == Key.Backspace)
            return DeleteLeft();

        if (key == Key.Delete)
            return DeleteRight();

        return false;
    }

    private bool MoveCaretHorizontal(int direction)
    {
        var nextOffset = GetHorizontalOffset(direction);
        if (CaretOffset != nextOffset)
        {
            CaretOffset = nextOffset;
            MarkInputDirty();
        }

        return true;
    }

    private bool MoveCaretVertical(int direction)
    {
        var nextOffset = GetVerticalOffset(direction);
        if (CaretOffset != nextOffset)
        {
            CaretOffset = nextOffset;
            MarkInputDirty();
        }
        return true;
    }

    private bool MoveCaretToLineBoundary(bool moveToEnd)
    {
        var nextOffset = GetLineBoundaryOffset(moveToEnd);
        if (CaretOffset != nextOffset)
        {
            CaretOffset = nextOffset;
            MarkInputDirty();
        }

        return true;
    }

    private bool DeleteLeft()
    {
        if (HasSelection)
        {
            ReplaceSelection("");
            MarkInputDirty();
            return true;
        }

        var text = Text?.ToString() ?? "";
        var offset = Math.Clamp(CaretOffset, 0, text.Length);
        if (offset <= 0)
            return true;

        var start = PreviousTextOffset(text, offset);
        Document?.Remove(start, offset - start);
        CaretOffset = start;
        MarkInputDirty();
        return true;
    }

    private bool DeleteRight()
    {
        if (HasSelection)
        {
            ReplaceSelection("");
            MarkInputDirty();
            return true;
        }

        var text = Text?.ToString() ?? "";
        var offset = Math.Clamp(CaretOffset, 0, text.Length);
        if (offset >= text.Length)
            return true;

        var end = NextTextOffset(text, offset);
        Document?.Remove(offset, end - offset);
        CaretOffset = offset;
        MarkInputDirty();
        return true;
    }

    private void MarkInputDirty()
    {
        SetNeedsDraw();
        SuperView?.SetNeedsDraw();
    }

    private int GetHorizontalOffset(int direction)
    {
        var text = Text?.ToString() ?? "";
        return direction < 0
            ? PreviousTextOffset(text, CaretOffset)
            : NextTextOffset(text, CaretOffset);
    }

    private int GetVerticalOffset(int direction)
    {
        var text = Text?.ToString() ?? "";
        var offset = Math.Clamp(CaretOffset, 0, text.Length);
        GetLineBounds(text, offset, out var lineStart, out var lineEnd);
        var column = Math.Max(0, offset - lineStart);

        if (direction < 0)
        {
            if (lineStart == 0)
                return CaretOffset;

            var previousLineEnd = lineStart - 1;
            if (previousLineEnd > 0 && text[previousLineEnd - 1] == '\r')
                previousLineEnd--;

            var previousLineBreak = previousLineEnd <= 0
                ? -1
                : text.LastIndexOf('\n', previousLineEnd - 1);
            var previousLineStart = previousLineBreak < 0 ? 0 : previousLineBreak + 1;
            return Math.Min(previousLineStart + column, previousLineEnd);
        }

        var nextBreak = text.IndexOf('\n', lineEnd);
        if (nextBreak < 0)
            return CaretOffset;

        var nextLineStart = nextBreak + 1;
        var nextLineEnd = text.IndexOf('\n', nextLineStart);
        if (nextLineEnd < 0)
            nextLineEnd = text.Length;
        if (nextLineEnd > nextLineStart && text[nextLineEnd - 1] == '\r')
            nextLineEnd--;

        return Math.Min(nextLineStart + column, nextLineEnd);
    }

    private int GetLineBoundaryOffset(bool moveToEnd)
    {
        var text = Text?.ToString() ?? "";
        GetLineBounds(text, CaretOffset, out var lineStart, out var lineEnd);
        return moveToEnd ? lineEnd : lineStart;
    }

    private void ExtendSelectionTo(int targetOffset)
    {
        var textLength = Document?.TextLength ?? 0;
        targetOffset = Math.Clamp(targetOffset, 0, textLength);

        while (CaretOffset != targetOffset)
        {
            var previousOffset = CaretOffset;
            ExtendSelectionBy(targetOffset > CaretOffset ? 1 : -1);
            if (CaretOffset == previousOffset)
                break;
        }

        MarkInputDirty();
    }

    private void ExtendSelectionBy(int direction)
    {
        if (extendCaretBy is not null)
        {
            extendCaretBy(direction);
            return;
        }

        InvokeCommand(direction < 0 ? Command.LeftExtend : Command.RightExtend);
    }

    private static Action<int>? CreateExtendCaretByDelegate(Editor editor)
    {
        try
        {
            return ExtendCaretByMethod?.CreateDelegate<Action<int>>(editor);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (MethodAccessException)
        {
            return null;
        }
    }

    private static void GetLineBounds(string text, int offset, out int start, out int end)
    {
        offset = Math.Clamp(offset, 0, text.Length);

        var previousBreak = offset == 0 ? -1 : text.LastIndexOf('\n', offset - 1);
        start = previousBreak < 0 ? 0 : previousBreak + 1;

        var nextBreak = text.IndexOf('\n', offset);
        end = nextBreak < 0 ? text.Length : nextBreak;
        if (end > start && text[end - 1] == '\r')
            end--;
    }

    private static int PreviousTextOffset(string text, int offset)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        if (offset <= 0)
            return 0;

        var current = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var next = current + rune.Utf16SequenceLength;
            if (next >= offset)
                return current;
            current = next;
        }

        return current;
    }

    private static int NextTextOffset(string text, int offset)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        if (offset >= text.Length)
            return text.Length;

        var current = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var next = current + rune.Utf16SequenceLength;
            if (next > offset)
                return next;
            current = next;
        }

        return text.Length;
    }

    private void DrawHint()
    {
        if (TuiSessionContext.App.Driver is null)
            return;

        var hint = HintProvider?.Invoke();
        if (string.IsNullOrWhiteSpace(hint))
            return;

        var text = Text?.ToString() ?? "";
        if (text.Contains('\n'))
            return;

        var prefixColumns = CellWidth(text);
        var hintText = "  " + hint;
        if (prefixColumns >= Viewport.Width - 1)
            return;

        SetAttribute(Palette.Dim);
        Move(prefixColumns, 0);
        DrawClipped(hintText, Viewport.Width - prefixColumns);
    }

    private static void DrawClipped(string text, int width)
    {
        var col = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (col >= width) break;
            if (Glyphs.GetColumns(rune) <= 0) continue;

            TuiSessionContext.App.Driver?.AddRune(Glyphs.Safe(rune));
            col++;
        }
    }

    private static int CellWidth(string text)
    {
        var width = 0;
        foreach (var rune in text.EnumerateRunes())
            width += Math.Max(0, Glyphs.GetColumns(rune));
        return width;
    }

    // Cursor-movement keys (without Shift) that should collapse an existing selection.
    private static readonly Key[] PlainNavigationKeys =
    [
        Key.CursorLeft, Key.CursorRight, Key.CursorUp, Key.CursorDown,
        Key.Home, Key.End, Key.PageUp, Key.PageDown,
        Key.CursorLeft.WithCtrl, Key.CursorRight.WithCtrl,
        Key.Home.WithCtrl, Key.End.WithCtrl,
    ];

    private static bool IsPlainNavigationKey(Key key)
    {
        foreach (var navKey in PlainNavigationKeys)
            if (key == navKey)
                return true;
        return false;
    }

    // Detect wide characters (emojis, CJK, combining marks) that render >1 cell wide.
    // These break Terminal.Gui's layout calculations when pasted.
    private static bool IsWideCharacter(Rune rune)
    {
        var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(rune.ToString(), 0);
        // Mark, Surrogate, PrivateUse categories can be problematic
        if (cat == System.Globalization.UnicodeCategory.Format ||
            cat == System.Globalization.UnicodeCategory.NonSpacingMark ||
            cat == System.Globalization.UnicodeCategory.EnclosingMark)
            return true;

        var c = rune.Value;
        // Emoji ranges and common wide blocks
        if ((c >= 0x1F300 && c <= 0x1F9FF) ||  // Emoji (modern block)
            (c >= 0x1F000 && c <= 0x1F02F) ||  // Emoticons
            (c >= 0x2600 && c <= 0x27BF) ||    // Misc symbols (includes U+274C)
            (c >= 0x2300 && c <= 0x23FF) ||    // Miscellaneous Technical
            (c >= 0x2B50 && c <= 0x2BFF) ||    // Miscellaneous Symbols and Pictographs
            (c >= 0x4E00 && c <= 0x9FFF) ||    // CJK unified ideographs
            (c >= 0x3040 && c <= 0x309F) ||    // Hiragana
            (c >= 0x30A0 && c <= 0x30FF) ||    // Katakana
            (c >= 0xAC00 && c <= 0xD7AF) ||    // Hangul
            (c >= 0xFE00 && c <= 0xFE0F) ||    // Variation selectors (emoji modifiers)
            (c == 0x200D) ||                   // Zero-width joiner
            (c == 0x200B) ||                   // Zero-width space
            (c == 0x200C) ||                   // Zero-width non-joiner
            (c == 0xFEFF))                     // Zero-width no-break space
            return true;

        return false;
    }

}
