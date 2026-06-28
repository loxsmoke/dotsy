using Dotsy.Cli.Tui;
using Dotsy.Cli.Tui.Colors;
using Terminal.Gui.Drawing;

namespace Dotsy.Cli.Tests;

[TestClass]
public sealed class AgentWindowConvoTests
{
    [TestMethod]
    public void WrapCellLine_PreservesSpacesAtStartOfWrappedLine()
    {
        var cells = Cells("word   next");

        var wrapped = AgentWindow.WrapCellLine(cells, width: 6);

        CollectionAssert.AreEqual(
            new[] { "word  ", " next" },
            wrapped.Select(Text).ToArray());
    }

    [TestMethod]
    public void StreamCursor_WrapsToNextLine_WhenFinalLineFillsViewport()
    {
        var lineWithCursor = AgentWindow.WithStreamCursor(Cells("123456"));

        var wrapped = AgentWindow.WrapCellLine(lineWithCursor, width: 6);

        CollectionAssert.AreEqual(
            new[] { "123456", "\u258c" },
            wrapped.Select(Text).ToArray());
    }

    private static List<Cell> Cells(string text) =>
        text.Select(ch => new Cell(Palette.Normal, false, ch.ToString())).ToList();

    private static string Text(IEnumerable<Cell> cells) =>
        string.Concat(cells.Select(c => c.Grapheme));
}
