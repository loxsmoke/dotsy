using Dotsy.Cli.Tui;

namespace Dotsy.Cli.Tests;

[TestClass]
public sealed class ScrollMathTests
{
    // ── MaxOffset (the scroll-to-end / bottom position) ──
    [TestMethod]
    [DataRow(100, 10, 90)]  // content taller than viewport
    [DataRow(10, 10, 0)]    // content exactly fills viewport
    [DataRow(5, 10, 0)]     // content shorter than viewport
    [DataRow(0, 10, 0)]     // empty
    public void MaxOffset_IsContentMinusViewport_FlooredAtZero(int content, int span, int expected)
    {
        Assert.AreEqual(expected, ScrollMath.MaxOffset(content, span));
    }

    // ── ClampOffset ──
    [TestMethod]
    [DataRow(50, 100, 10, 50)]   // within range
    [DataRow(95, 100, 10, 90)]   // above max → clamped to MaxOffset
    [DataRow(-5, 100, 10, 0)]    // below zero → clamped to 0
    [DataRow(3, 5, 10, 0)]       // content fits → only 0 is valid
    public void ClampOffset_KeepsWithinValidRange(int desired, int content, int span, int expected)
    {
        Assert.AreEqual(expected, ScrollMath.ClampOffset(desired, content, span));
    }

    // ── CanScroll ──
    [TestMethod]
    [DataRow(100, 10, true)]
    [DataRow(11, 10, true)]
    [DataRow(10, 10, false)]   // exactly fills → nothing to scroll
    [DataRow(5, 10, false)]
    [DataRow(2, 1, true)]
    [DataRow(1, 1, false)]     // span floored at 1, 1 > 1 is false
    public void CanScroll_TrueWhenContentExceedsViewport(int content, int span, bool expected)
    {
        Assert.AreEqual(expected, ScrollMath.CanScroll(content, span));
    }

    // ── PageStep ──
    [TestMethod]
    [DataRow(10, 9)]
    [DataRow(2, 1)]
    [DataRow(1, 1)]   // floored at 1
    [DataRow(0, 1)]
    public void PageStep_IsViewportMinusOne_FlooredAtOne(int span, int expected)
    {
        Assert.AreEqual(expected, ScrollMath.PageStep(span));
    }

    // ── PageTop ──
    // viewport 10 → page step 9; content 100 → maxTop 90.

    [TestMethod]
    public void PageTop_FromTop_Down_MovesFullPage_NotOneLine()
    {
        // Regression: the first PageDown at the top must advance a full page (9), not nudge 1 line.
        Assert.AreEqual(9, ScrollMath.PageTop(currentTop: 0, viewportHeight: 10, contentLineCount: 100, down: true));
    }

    [TestMethod]
    public void PageTop_FromTop_Up_StaysAtTop()
    {
        Assert.AreEqual(0, ScrollMath.PageTop(currentTop: 0, viewportHeight: 10, contentLineCount: 100, down: false));
    }

    [TestMethod]
    public void PageTop_FromBottom_Up_MovesFullPage()
    {
        Assert.AreEqual(81, ScrollMath.PageTop(currentTop: 90, viewportHeight: 10, contentLineCount: 100, down: false));
    }

    [TestMethod]
    public void PageTop_FromBottom_Down_StaysClampedAtMaxTop()
    {
        Assert.AreEqual(90, ScrollMath.PageTop(currentTop: 90, viewportHeight: 10, contentLineCount: 100, down: true));
    }

    [TestMethod]
    public void PageTop_Down_ClampsToMaxTop()
    {
        // Near the bottom: a full page would overshoot, so it clamps to maxTop (90).
        Assert.AreEqual(90, ScrollMath.PageTop(currentTop: 85, viewportHeight: 10, contentLineCount: 100, down: true));
    }

    [TestMethod]
    public void PageTop_ContentFitsViewport_AlwaysZero()
    {
        Assert.AreEqual(0, ScrollMath.PageTop(currentTop: 0, viewportHeight: 10, contentLineCount: 5, down: true));
        Assert.AreEqual(0, ScrollMath.PageTop(currentTop: 0, viewportHeight: 10, contentLineCount: 5, down: false));
    }

    [TestMethod]
    public void PageTop_TinyViewport_StepIsAtLeastOne()
    {
        // viewportHeight 1 → pageStep clamps to 1; content 10 → maxTop 9.
        Assert.AreEqual(1, ScrollMath.PageTop(currentTop: 0, viewportHeight: 1, contentLineCount: 10, down: true));
        Assert.AreEqual(4, ScrollMath.PageTop(currentTop: 5, viewportHeight: 1, contentLineCount: 10, down: false));
    }

    [TestMethod]
    public void EnsureVisibleTop_WhenSelectionMovesBelowViewport_ScrollsDownJustEnough()
    {
        Assert.AreEqual(3, ScrollMath.EnsureVisibleTop(currentTop: 0, selectedIndex: 7, viewportHeight: 5, contentLineCount: 20));
    }

    [TestMethod]
    public void EnsureVisibleTop_WhenSelectionMovesAboveViewport_ScrollsUpToSelection()
    {
        Assert.AreEqual(2, ScrollMath.EnsureVisibleTop(currentTop: 6, selectedIndex: 2, viewportHeight: 5, contentLineCount: 20));
    }

    [TestMethod]
    public void EnsureVisibleTop_WhenSelectionAlreadyVisible_KeepsCurrentTop()
    {
        Assert.AreEqual(6, ScrollMath.EnsureVisibleTop(currentTop: 6, selectedIndex: 8, viewportHeight: 5, contentLineCount: 20));
    }

    [TestMethod]
    public void EnsureVisibleTop_ClampsTopWhenContentShrinks()
    {
        Assert.AreEqual(1, ScrollMath.EnsureVisibleTop(currentTop: 8, selectedIndex: 3, viewportHeight: 4, contentLineCount: 5));
    }
}
