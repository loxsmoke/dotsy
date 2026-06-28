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
    public void CtrlCWithoutSelection_RaisesCancelRequested()
    {
        using var input = new MultilineInput();
        var requested = false;
        input.CancelRequested += (_, _) => requested = true;

        var handled = InvokeOnKeyDown(input, Key.C.WithCtrl);

        Assert.IsTrue(handled);
        Assert.IsTrue(requested);
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
    }

    private static bool InvokeOnKeyDown(MultilineInput input, Key key)
    {
        var method = typeof(MultilineInput).GetMethod(
            "OnKeyDown",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (bool)method.Invoke(input, [key])!;
    }
}
