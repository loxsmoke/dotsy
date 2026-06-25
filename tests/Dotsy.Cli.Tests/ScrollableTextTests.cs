using System.Drawing;
using System.Reflection;
using Dotsy.Cli.Tui;
using Dotsy.Cli.Tui.Colors;
using Terminal.Gui.Drawing;
using Terminal.Gui.Editor;
using Terminal.Gui.Input;

namespace Dotsy.Cli.Tests;

[TestClass]
public sealed class ScrollableTextTests
{
    [TestMethod]
    public void LoadText_PreservesPlainText()
    {
        using var view = new ScrollableText();

        view.LoadText(Lines("one", "two"));

        Assert.AreEqual("one\ntwo", view.Text.ToString());
    }

    [TestMethod]
    public void CanScrollVertical_IsTrueWhenContentExceedsViewportHeight()
    {
        using var view = new ScrollableText
        {
            Viewport = new Rectangle(0, 0, 20, 2)
        };
        view.LoadText(Lines("one", "two", "three"));

        Assert.IsTrue(view.CanScrollVertical());
    }

    [TestMethod]
    public void CanScrollVertical_IsFalseWhenContentFitsViewportHeight()
    {
        using var view = new ScrollableText
        {
            Viewport = new Rectangle(0, 0, 20, 5)
        };
        view.LoadText(Lines("one", "two"));

        Assert.IsFalse(view.CanScrollVertical());
    }

    [TestMethod]
    public void MoveEnd_ScrollsToLastPossibleLineAndResetsHorizontalOffset()
    {
        using var view = new ScrollableText
        {
            Viewport = new Rectangle(4, 0, 20, 3)
        };
        view.LoadText(Lines("one", "two", "three", "four", "five"));

        view.MoveEnd();

        Assert.AreEqual(0, view.Viewport.X);
        Assert.AreEqual(2, view.Viewport.Y);
    }

    [TestMethod]
    public void ScrollTo_ClampsOffsetsToValidViewport()
    {
        using var view = new ScrollableText
        {
            Viewport = new Rectangle(0, 0, 20, 3)
        };

        view.ScrollTo(new Point(-5, -2));

        Assert.AreEqual(0, view.Viewport.X);
        Assert.AreEqual(0, view.Viewport.Y);

        view.LoadText(Lines("one", "two", "three", "four", "five"));
        view.ScrollTo(new Point(0, 4));

        Assert.AreEqual(0, view.Viewport.X);
        Assert.AreEqual(2, view.Viewport.Y);
    }

    [TestMethod]
    public void EndKey_ScrollsToBottomWhenSelectionCopyIsDisabled()
    {
        using var view = new ScrollableText
        {
            Viewport = new Rectangle(0, 0, 20, 2)
        };
        view.LoadText(Lines("one", "two", "three", "four"));

        var handled = InvokeOnKeyDown(view, Key.End);

        Assert.IsTrue(handled);
        Assert.AreEqual(2, view.Viewport.Y);
    }

    [TestMethod]
    public void CtrlRightKey_ScrollsHorizontallyWhenContentIsWide()
    {
        using var view = new ScrollableText
        {
            Viewport = new Rectangle(0, 0, 5, 3)
        };
        view.LoadText(Lines("abcdefghijklmnopqrstuvwxyz"));

        var handled = InvokeOnKeyDown(view, Key.CursorRight.WithCtrl);

        Assert.IsTrue(handled);
        Assert.AreEqual(10, view.Viewport.X);
    }

    private static List<List<Cell>> Lines(params string[] lines) =>
        lines
            .Select(line => line.Select(ch => new Cell(Palette.Normal, false, ch.ToString())).ToList())
            .ToList();

    private static bool InvokeOnKeyDown(ScrollableText view, Key key)
    {
        var method = typeof(ScrollableText).GetMethod(
            "OnKeyDown",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (bool)method.Invoke(view, [key])!;
    }
}
