using Dotsy.Cli.Tui.Approval;

namespace Dotsy.Cli.Tests;

[TestClass]
public sealed class ApprovalViewTests
{
    // FitMessage guards the approval dialog against multi-line/oversized messages painting over
    // the button row (seen with a Task call whose raw JSON prompt spanned four rows).

    [TestMethod]
    public void FitMessage_ShortText_PassesThroughWithCollapsedSpacing()
    {
        Assert.AreEqual("Edit file.cs lines 1-3", ApprovalView.FitMessage("Edit  file.cs lines 1-3", 120));
    }

    [TestMethod]
    public void FitMessage_CollapsesNewlinesAndRepeatedWhitespace()
    {
        var fitted = ApprovalView.FitMessage("Task  {\"prompt\":\"line one\r\nline two\n\nline\tthree\"}", 200);

        Assert.IsFalse(fitted.Contains('\n'));
        Assert.IsFalse(fitted.Contains('\r'));
        Assert.IsFalse(fitted.Contains('\t'));
        StringAssert.Contains(fitted, "line one line two line three");
    }

    [TestMethod]
    public void FitMessage_TruncatesToTwoRowBudget()
    {
        // frameWidth 86 -> line width 80 -> two-row budget 160.
        var fitted = ApprovalView.FitMessage(new string('x', 500), 86);

        Assert.AreEqual(160, fitted.Length);
        StringAssert.EndsWith(fitted, "…");
    }

    [TestMethod]
    public void FitMessage_UnknownFrameWidth_UsesConservativeBudget()
    {
        // Before the first layout Frame.Width can be 0; the fallback budget is 120 * 2.
        var fitted = ApprovalView.FitMessage(new string('x', 500), 0);

        Assert.AreEqual(240, fitted.Length);
        StringAssert.EndsWith(fitted, "…");
    }
}
