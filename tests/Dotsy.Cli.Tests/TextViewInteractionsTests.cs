using Dotsy.Cli.Tui;
using Terminal.Gui.Input;

namespace Dotsy.Cli.Tests;

[TestClass]
public sealed class TextViewInteractionsTests
{
    [TestMethod]
    public void TryGetShiftSelectionCommand_MapsEverySupportedNavigationKey()
    {
        (Key Key, Command Command)[] expected =
        [
            (Key.CursorLeft.WithShift, Command.LeftExtend),
            (Key.CursorRight.WithShift, Command.RightExtend),
            (Key.CursorUp.WithShift, Command.UpExtend),
            (Key.CursorDown.WithShift, Command.DownExtend),
            (Key.Home.WithShift, Command.LeftStartExtend),
            (Key.End.WithShift, Command.RightEndExtend),
            (Key.PageUp.WithShift, Command.PageUpExtend),
            (Key.PageDown.WithShift, Command.PageDownExtend),
            (Key.CursorLeft.WithCtrl.WithShift, Command.WordLeftExtend),
            (Key.CursorRight.WithCtrl.WithShift, Command.WordRightExtend),
            (Key.Home.WithCtrl.WithShift, Command.StartExtend),
            (Key.End.WithCtrl.WithShift, Command.EndExtend),
        ];

        foreach (var (key, expectedCommand) in expected)
        {
            Assert.IsTrue(TextViewInteractions.TryGetShiftSelectionCommand(key, out var command));
            Assert.AreEqual(expectedCommand, command);
        }

        Assert.IsFalse(TextViewInteractions.TryGetShiftSelectionCommand(Key.Enter, out _));
    }

    [TestMethod]
    public void IsContextMenuMouseEvent_RecognizesConfiguredAndRightButtonFlags()
    {
        var configured = MouseFlags.LeftButtonClicked;
        Assert.IsTrue(TextViewInteractions.IsContextMenuMouseEvent(configured, configured));

        MouseFlags[] rightButtonFlags =
        [
            MouseFlags.RightButtonPressed,
            MouseFlags.RightButtonReleased,
            MouseFlags.RightButtonClicked,
            MouseFlags.RightButtonDoubleClicked,
            MouseFlags.RightButtonTripleClicked,
        ];

        foreach (var flags in rightButtonFlags)
            Assert.IsTrue(TextViewInteractions.IsContextMenuMouseEvent(flags, null));

        Assert.IsFalse(TextViewInteractions.IsContextMenuMouseEvent(MouseFlags.LeftButtonClicked, null));
    }
}
