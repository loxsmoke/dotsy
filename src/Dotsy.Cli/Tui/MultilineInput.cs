using Dotsy.Cli.Tui.Colors;
using System.Text;
using Terminal.Gui.Editor;

namespace Dotsy.Cli.Tui;

// Editable multi-line input that submits on Enter and navigates command history with Up/Down.
// Printable characters are batched with a short timer so that a paste flood arrives as a
// single InsertText call.  A newline inside a paste stream is stored literally rather than
// triggering a submit.
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
    public event EventHandler?         CancelRequested;  // Ctrl+C

    private readonly StringBuilder pasteBuffer = new();
    private Timer? flushTimer;
    private const int PasteFlushMs = 5;

    // Pre-built Key objects for modifier combos; avoids allocating on every keypress.
    private static readonly Key CtrlQ = new Key(KeyCode.Q).WithCtrl;
    private static readonly Key CtrlC = new Key(KeyCode.C).WithCtrl;
    private static readonly Key CtrlInsert = Key.InsertChar.WithCtrl;

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
        InvokeCommand(Command.End);
        SetNeedsDraw();
    }

    public void InsertText(string text)
    {
        if (HasSelection)
            ReplaceSelection(text);
        else
            Document?.Insert(CaretOffset, text);
    }

    public void SetTextAndMoveEnd(string text)
    {
        FlushPasteBuffer();
        Text = text;
        InvokeCommand(Command.End);
        SetNeedsDraw();
    }

    protected override bool OnKeyDown(Key key)
    {
        // Shift+navigation extends the selection. Invoke the matching TextView "extend" command
        // directly instead of relying on the bound key being matched; selection then works even
        // where the shift+arrow keybinding lookup misbehaves.
        foreach (var (selKey, command) in ShiftSelectionCommands)
        {
            if (key == selKey)
            {
                FlushPasteBuffer();
                InvokeCommand(command);
                key.Handled = true;
                return true;
            }
        }

        // Plain navigation collapses any active selection (from Shift, Ctrl+A, or the mouse).
        // TextView only auto-clears selections it started itself via Shift, so clear it here for
        // selections made by SelectAll or the mouse before letting the cursor move normally.
        if (HasSelection && IsPlainNavigationKey(key))
            ClearSelection();

        // Enter: submit, or buffer as a literal newline while mid-paste.
        if (key.KeyCode == KeyCode.Enter)
        {
            if (pasteBuffer.Length > 0)
            {
                pasteBuffer.Append('\n');
                ResetFlushTimer();
            }
            else
            {
                var text = Text?.ToString().Trim() ?? "";
                if (!string.IsNullOrEmpty(text))
                    Submitted?.Invoke(this, text);
            }
            key.Handled = true;
            return true;
        }

        // CursorUp: navigate history from the first line; otherwise move cursor normally.
        if (key.KeyCode == KeyCode.CursorUp)
        {
            if ((Document?.GetLineByOffset(CaretOffset)?.LineNumber ?? 1) - 1 == 0)
            {
                FlushPasteBuffer();
                HistoryPrev?.Invoke(this, EventArgs.Empty);
                key.Handled = true;
                return true;
            }
            // Interior line; let InvokeCommands (Command.Up) move the cursor.
            FlushPasteBuffer();
            return base.OnKeyDown(key);
        }

        // CursorDown: navigate history from the last line; otherwise move cursor normally.
        if (key.KeyCode == KeyCode.CursorDown)
        {
            var rawText = Text?.ToString() ?? "";
            int lastRow = Math.Max(0, rawText.TrimEnd('\n', '\r').Count(c => c == '\n'));
            if ((Document?.GetLineByOffset(CaretOffset)?.LineNumber ?? 1) - 1 >= lastRow)
            {
                FlushPasteBuffer();
                HistoryNext?.Invoke(this, EventArgs.Empty);
                key.Handled = true;
                return true;
            }
            FlushPasteBuffer();
            return base.OnKeyDown(key);
        }

        // Ctrl+Q: quit.  Must use Key == (not key.IsCtrl && key.KeyCode == KeyCode.Q) because
        // TG v2 encodes modifier combos with the mask in KeyCode (Key.Q.WithCtrl binding).
        if (key == CtrlQ)
        {
            QuitRequested?.Invoke(this, EventArgs.Empty);
            key.Handled = true;
            return true;
        }

        // Ctrl+C: cancel running agent.
        if (key == CtrlC)
        {
            if (HasSelection)
            {
                InvokeCommand(Command.Copy);
                key.Handled = true;
                return true;
            }

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

        // Printable chars: buffer for paste coalescing. Skip wide characters (emojis, etc).
        if (!key.IsCtrl && !key.IsAlt && key.AsRune.Value >= 32 && !IsWideCharacter(key.AsRune))
        {
            pasteBuffer.Append(key.AsRune.ToString());
            ResetFlushTimer();
            key.Handled = true;
            return true;
        }

        // Everything else (Backspace, Delete, Left, Right, Ctrl+Z, etc.): flush the buffer
        // so the view's content is current, then let TextView handle the key normally.
        FlushPasteBuffer();
        return base.OnKeyDown(key);
    }

    protected override bool OnMouseEvent(Mouse ev)
    {
        if (IsContextMenuMouseEvent(ev))
        {
            ev.Handled = true;
            return true;
        }

        return base.OnMouseEvent(ev);
    }

    private void ResetFlushTimer()
    {
        flushTimer?.Dispose();
        flushTimer = new Timer(
            _ => TuiSessionContext.App.Invoke(FlushPasteBuffer),
            null, PasteFlushMs, System.Threading.Timeout.Infinite);
    }

    private void FlushPasteBuffer()
    {
        flushTimer?.Dispose();
        flushTimer = null;
        if (pasteBuffer.Length == 0) return;
        // Replace emoji-presentation runes: the terminal renders them 2 columns while TextView's
        // cursor model counts 1, which desyncs editing (cursor jumps lines, text inserts off-place).
        var text = Glyphs.Sanitize(pasteBuffer.ToString());
        pasteBuffer.Clear();
        if (HasSelection)
            ReplaceSelection(text);
        else
            Document?.Insert(CaretOffset, text);
    }

    private bool IsContextMenuMouseEvent(Mouse ev) =>
        ev.Flags == ContextMenu?.MouseFlags
        || ev.Flags.HasFlag(MouseFlags.RightButtonPressed)
        || ev.Flags.HasFlag(MouseFlags.RightButtonReleased)
        || ev.Flags.HasFlag(MouseFlags.RightButtonClicked)
        || ev.Flags.HasFlag(MouseFlags.RightButtonDoubleClicked)
        || ev.Flags.HasFlag(MouseFlags.RightButtonTripleClicked);

    // Shift+navigation keys mapped to the TextView command that extends the selection.
    private static readonly (Key Key, Command Command)[] ShiftSelectionCommands =
    [
        (Key.CursorLeft.WithShift,           Command.LeftExtend),
        (Key.CursorRight.WithShift,          Command.RightExtend),
        (Key.CursorUp.WithShift,             Command.UpExtend),
        (Key.CursorDown.WithShift,           Command.DownExtend),
        (Key.Home.WithShift,                 Command.LeftStartExtend),
        (Key.End.WithShift,                  Command.RightEndExtend),
        (Key.PageUp.WithShift,               Command.PageUpExtend),
        (Key.PageDown.WithShift,             Command.PageDownExtend),
        (Key.CursorLeft.WithCtrl.WithShift,  Command.WordLeftExtend),
        (Key.CursorRight.WithCtrl.WithShift, Command.WordRightExtend),
        (Key.Home.WithCtrl.WithShift,        Command.StartExtend),
        (Key.End.WithCtrl.WithShift,         Command.EndExtend),
    ];

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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            flushTimer?.Dispose();
            flushTimer = null;
        }
        base.Dispose(disposing);
    }
}
