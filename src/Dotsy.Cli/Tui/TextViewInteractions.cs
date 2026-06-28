using Terminal.Gui.Input;

namespace Dotsy.Cli.Tui;

internal static class TextViewInteractions
{
    private static readonly (Key Key, Command Command)[] ShiftSelectionCommands =
    [
        (Key.CursorLeft.WithShift,           Command.LeftExtend),
        (Key.CursorRight.WithShift,          Command.RightExtend),
        (Key.CursorUp.WithShift,             Command.UpExtend),
        (Key.CursorDown.WithShift,           Command.DownExtend),
        (Key.Home.WithShift,                 Command.LeftStartExtend),
        (Key.End.WithShift,                  Command.RightEndExtend),
        (Key.PageUp.WithShift,               Command.PageUpExtend),
        (Key.PageDown.WithShift,              Command.PageDownExtend),
        (Key.CursorLeft.WithCtrl.WithShift,  Command.WordLeftExtend),
        (Key.CursorRight.WithCtrl.WithShift, Command.WordRightExtend),
        (Key.Home.WithCtrl.WithShift,        Command.StartExtend),
        (Key.End.WithCtrl.WithShift,         Command.EndExtend),
    ];

    public static bool TryGetShiftSelectionCommand(Key key, out Command command)
    {
        var match = ShiftSelectionCommands
            .Where(mapping => key == mapping.Key)
            .Select(mapping => (Command?)mapping.Command)
            .FirstOrDefault();

        command = match.GetValueOrDefault();
        return match.HasValue;
    }

    public static bool IsContextMenuMouseEvent(
        MouseFlags flags,
        MouseFlags? contextMenuFlags) =>
        flags == contextMenuFlags
        || flags.HasFlag(MouseFlags.RightButtonPressed)
        || flags.HasFlag(MouseFlags.RightButtonReleased)
        || flags.HasFlag(MouseFlags.RightButtonClicked)
        || flags.HasFlag(MouseFlags.RightButtonDoubleClicked)
        || flags.HasFlag(MouseFlags.RightButtonTripleClicked);
}
