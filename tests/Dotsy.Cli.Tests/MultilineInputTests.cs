using System.Reflection;
using Dotsy.Cli.Tui;
using Terminal.Gui.Input;

namespace Dotsy.Cli.Tests;

[TestClass]
public sealed class MultilineInputTests
{
    [TestMethod]
    public void Enter_SubmitsTrimmedTextWhenInputIsNotEmpty()
    {
        using var input = new MultilineInput();
        string? submitted = null;
        input.Submitted += (_, text) => submitted = text;
        input.Text = "  hello dotsy  ";

        var handled = InvokeOnKeyDown(input, Key.Enter);

        Assert.IsTrue(handled);
        Assert.AreEqual("hello dotsy", submitted);
    }

    [TestMethod]
    public void Enter_DoesNotSubmitWhitespaceOnlyText()
    {
        using var input = new MultilineInput();
        var submitted = false;
        input.Submitted += (_, _) => submitted = true;
        input.Text = "   ";

        var handled = InvokeOnKeyDown(input, Key.Enter);

        Assert.IsTrue(handled);
        Assert.IsFalse(submitted);
    }

    [TestMethod]
    public void KeyInterceptor_HandlesEnterBeforeSubmit()
    {
        using var input = new MultilineInput();
        var submitted = false;
        input.Submitted += (_, _) => submitted = true;
        input.KeyInterceptor = key => key == Key.Enter;
        input.Text = "hello";

        var handled = InvokeOnKeyDown(input, Key.Enter);

        Assert.IsTrue(handled);
        Assert.IsFalse(submitted);
    }

    [TestMethod]
    public void KeyInterceptor_HandlesArrowsBeforeHistory()
    {
        using var input = new MultilineInput();
        var historyRequested = false;
        input.HistoryPrev += (_, _) => historyRequested = true;
        input.KeyInterceptor = key => key == Key.CursorUp;
        input.Text = "hello";
        input.CaretOffset = 0;

        var handled = InvokeOnKeyDown(input, Key.CursorUp);

        Assert.IsTrue(handled);
        Assert.IsFalse(historyRequested);
    }

    [TestMethod]
    public void CtrlQ_RaisesQuitRequested()
    {
        using var input = new MultilineInput();
        var requested = false;
        input.QuitRequested += (_, _) => requested = true;

        var handled = InvokeOnKeyDown(input, Key.Q.WithCtrl);

        Assert.IsTrue(handled);
        Assert.IsTrue(requested);
    }

    [TestMethod]
    public void CtrlG_RaisesCancelRequested()
    {
        using var input = new MultilineInput();
        var requested = false;
        input.CancelRequested += (_, _) => requested = true;

        var handled = InvokeOnKeyDown(input, Key.G.WithCtrl);

        Assert.IsTrue(handled);
        Assert.IsTrue(requested);
    }

    [TestMethod]
    public void CtrlCWithoutSelection_DoesNotRaiseCancelRequested()
    {
        using var input = new MultilineInput();
        var requested = false;
        input.CancelRequested += (_, _) => requested = true;

        var handled = InvokeOnKeyDown(input, Key.C.WithCtrl);

        Assert.IsTrue(handled);
        Assert.IsFalse(requested);
    }

    [TestMethod]
    public void TabAndEscapeBubbleToHost()
    {
        using var input = new MultilineInput();

        Assert.IsFalse(InvokeOnKeyDown(input, Key.Tab));
        Assert.IsFalse(InvokeOnKeyDown(input, Key.Tab.WithShift));
        Assert.IsFalse(InvokeOnKeyDown(input, Key.Esc));
    }

    [TestMethod]
    public void UpOnFirstLineRaisesHistoryPrev()
    {
        using var input = new MultilineInput();
        var requested = false;
        input.HistoryPrev += (_, _) => requested = true;
        input.Text = "first\nsecond";
        input.CaretOffset = 0;

        var handled = InvokeOnKeyDown(input, Key.CursorUp);

        Assert.IsTrue(handled);
        Assert.IsTrue(requested);
    }

    [TestMethod]
    public void DownOnLastLineRaisesHistoryNext()
    {
        using var input = new MultilineInput();
        var requested = false;
        input.HistoryNext += (_, _) => requested = true;
        input.Text = "first\nsecond";
        input.CaretOffset = input.Text.Length;

        var handled = InvokeOnKeyDown(input, Key.CursorDown);

        Assert.IsTrue(handled);
        Assert.IsTrue(requested);
    }

    [TestMethod]
    public void SetTextAndMoveEnd_ReplacesText()
    {
        using var input = new MultilineInput();

        input.SetTextAndMoveEnd("replacement");

        Assert.AreEqual("replacement", input.Text.ToString());
    }

    [TestMethod]
    public void InsertText_AppendsAtCaret()
    {
        using var input = new MultilineInput();
        input.Text = "hello";
        input.CaretOffset = input.Text.Length;

        input.InsertText(" world");

        Assert.AreEqual("hello world", input.Text.ToString());
        Assert.AreEqual("hello world".Length, input.CaretOffset);
    }

    [TestMethod]
    public void PrintableKey_InsertsImmediately()
    {
        using var input = new MultilineInput();

        var handled = InvokeOnKeyDown(input, Key.A);

        Assert.IsTrue(handled);
        Assert.AreEqual("a", input.Text.ToString());
        Assert.AreEqual(1, input.CaretOffset);
    }

    [TestMethod]
    public void ConsecutivePrintableKeys_InsertInOrder()
    {
        using var input = new MultilineInput();

        InvokeOnKeyDown(input, Key.A);
        InvokeOnKeyDown(input, Key.B);

        Assert.AreEqual("ab", input.Text.ToString());
        Assert.AreEqual(2, input.CaretOffset);
    }

    [TestMethod]
    public void CursorLeft_MovesCaretImmediately()
    {
        using var input = new MultilineInput();
        input.Text = "abc";
        input.CaretOffset = 2;

        var handled = InvokeOnKeyDown(input, Key.CursorLeft);

        Assert.IsTrue(handled);
        Assert.AreEqual(1, input.CaretOffset);
    }

    [TestMethod]
    public void CursorRight_MovesCaretImmediately()
    {
        using var input = new MultilineInput();
        input.Text = "abc";
        input.CaretOffset = 1;

        var handled = InvokeOnKeyDown(input, Key.CursorRight);

        Assert.IsTrue(handled);
        Assert.AreEqual(2, input.CaretOffset);
    }

    [TestMethod]
    public void Backspace_RemovesPreviousCharacterImmediately()
    {
        using var input = new MultilineInput();
        input.Text = "abc";
        input.CaretOffset = 2;

        var handled = InvokeOnKeyDown(input, Key.Backspace);

        Assert.IsTrue(handled);
        Assert.AreEqual("ac", input.Text.ToString());
        Assert.AreEqual(1, input.CaretOffset);
    }

    [TestMethod]
    public void Delete_RemovesCurrentCharacterImmediately()
    {
        using var input = new MultilineInput();
        input.Text = "abc";
        input.CaretOffset = 1;

        var handled = InvokeOnKeyDown(input, Key.Delete);

        Assert.IsTrue(handled);
        Assert.AreEqual("ac", input.Text.ToString());
        Assert.AreEqual(1, input.CaretOffset);
    }

    [TestMethod]
    public void UpInsideMultilineInput_MovesCaretImmediately()
    {
        using var input = new MultilineInput();
        input.Text = "abc\ndef";
        input.CaretOffset = 6;

        var handled = InvokeOnKeyDown(input, Key.CursorUp);

        Assert.IsTrue(handled);
        Assert.AreEqual(2, input.CaretOffset);
    }

    [TestMethod]
    public void DownInsideMultilineInput_MovesCaretImmediately()
    {
        using var input = new MultilineInput();
        input.Text = "abc\ndef";
        input.CaretOffset = 2;

        var handled = InvokeOnKeyDown(input, Key.CursorDown);

        Assert.IsTrue(handled);
        Assert.AreEqual(6, input.CaretOffset);
    }

    [TestMethod]
    public void FastSelectionHelper_BindsTerminalGuiCaretExtension()
    {
        using var input = new MultilineInput();
        var field = typeof(MultilineInput).GetField(
            "extendCaretBy",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        Assert.IsNotNull(field.GetValue(input));
    }

    [TestMethod]
    public void ShiftRight_SelectsNextCharacterImmediately()
    {
        using var input = new MultilineInput();
        input.Text = "abcd";
        input.CaretOffset = 1;

        var handled = InvokeOnKeyDown(input, Key.CursorRight.WithShift);

        Assert.IsTrue(handled);
        Assert.IsTrue(input.HasSelection);
        Assert.AreEqual(1, input.SelectionStart);
        Assert.AreEqual(2, input.SelectionEnd);
        Assert.AreEqual(2, input.CaretOffset);
    }

    [TestMethod]
    public void ShiftLeft_SelectsPreviousCharacterImmediately()
    {
        using var input = new MultilineInput();
        input.Text = "abcd";
        input.CaretOffset = 2;

        var handled = InvokeOnKeyDown(input, Key.CursorLeft.WithShift);

        Assert.IsTrue(handled);
        Assert.IsTrue(input.HasSelection);
        Assert.AreEqual(1, input.SelectionStart);
        Assert.AreEqual(2, input.SelectionEnd);
        Assert.AreEqual(1, input.CaretOffset);
    }

    [TestMethod]
    public void ConsecutiveShiftRight_ExtendsSelectionImmediately()
    {
        using var input = new MultilineInput();
        input.Text = "abcd";
        input.CaretOffset = 1;

        InvokeOnKeyDown(input, Key.CursorRight.WithShift);
        InvokeOnKeyDown(input, Key.CursorRight.WithShift);

        Assert.IsTrue(input.HasSelection);
        Assert.AreEqual(1, input.SelectionStart);
        Assert.AreEqual(3, input.SelectionEnd);
        Assert.AreEqual(3, input.CaretOffset);
    }

    [TestMethod]
    public void ShiftRightThenShiftLeft_ClearsSelectionAtAnchor()
    {
        using var input = new MultilineInput();
        input.Text = "abcd";
        input.CaretOffset = 1;

        InvokeOnKeyDown(input, Key.CursorRight.WithShift);
        InvokeOnKeyDown(input, Key.CursorLeft.WithShift);

        Assert.IsFalse(input.HasSelection);
        Assert.AreEqual(1, input.CaretOffset);
    }

    [TestMethod]
    public void ShiftDownInsideMultilineInput_ExtendsSelectionImmediately()
    {
        using var input = new MultilineInput();
        input.Text = "abc\ndef";
        input.CaretOffset = 2;

        var handled = InvokeOnKeyDown(input, Key.CursorDown.WithShift);

        Assert.IsTrue(handled);
        Assert.IsTrue(input.HasSelection);
        Assert.AreEqual(2, input.SelectionStart);
        Assert.AreEqual(6, input.SelectionEnd);
        Assert.AreEqual(6, input.CaretOffset);
    }

    private static bool InvokeOnKeyDown(MultilineInput input, Key key)
    {
        var method = typeof(MultilineInput).GetMethod(
            "OnKeyDown",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (bool)method.Invoke(input, [key])!;
    }
}
