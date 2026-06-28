using Dotsy.Core.Tools;

namespace Dotsy.Core.Tests;

[TestClass]
public sealed class EditSummaryFormatterTests
{
    [TestMethod]
    [DataRow("", 0)]
    [DataRow("one", 1)]
    [DataRow("one\n", 1)]
    [DataRow("one\ntwo", 2)]
    [DataRow("one\ntwo\n", 2)]
    public void CountLines_UsesPanelLineSemantics(string text, int expected)
    {
        Assert.AreEqual(expected, EditSummaryFormatter.CountLines(text));
    }

    [TestMethod]
    [DataRow(0, 0, "")]
    [DataRow(2, 0, "  +2 lines")]
    [DataRow(0, 3, "  -3 lines")]
    [DataRow(2, 3, "  +2 -3 lines")]
    public void LineDelta_FormatsNonZeroCounts(int added, int deleted, string expected)
    {
        Assert.AreEqual(expected, EditSummaryFormatter.LineDelta(added, deleted));
    }
}
