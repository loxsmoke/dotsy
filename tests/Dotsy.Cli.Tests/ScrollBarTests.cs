using Dotsy.Cli.Tui;

namespace Dotsy.Cli.Tests;

[TestClass]
public sealed class ScrollBarTests
{
    [TestMethod]
    [DataRow(0)]
    [DataRow(-3)]
    public void Build_NonPositiveLength_ReturnsEmpty(int length)
    {
        Assert.AreEqual("", ScrollBar.Build(horizontal: false, length, 100, 10, 0));
    }

    [TestMethod]
    public void Build_LengthMatchesRequested()
    {
        for (int len = 1; len <= 20; len++)
        {
            var bar = ScrollBar.Build(horizontal: false, length: len, total: 100, viewportSpan: 10, offset: 0);
            Assert.AreEqual(len, bar.Length, $"length {len}");
        }
    }

    [TestMethod]
    public void Build_SingleCell_IsTrack()
    {
        Assert.AreEqual("░", ScrollBar.Build(horizontal: false, length: 1, total: 100, viewportSpan: 10, offset: 0));
        Assert.AreEqual("░", ScrollBar.Build(horizontal: true, length: 1, total: 100, viewportSpan: 10, offset: 0));
    }

    [TestMethod]
    public void Build_TwoCells_AreCapsOnly()
    {
        Assert.AreEqual("▲▼", ScrollBar.Build(horizontal: false, length: 2, total: 100, viewportSpan: 10, offset: 0));
        Assert.AreEqual("◄►", ScrollBar.Build(horizontal: true, length: 2, total: 100, viewportSpan: 10, offset: 0));
    }

    [TestMethod]
    [DataRow(false, '▲', '▼')]
    [DataRow(true, '◄', '►')]
    public void Build_UsesExpectedCaps(bool horizontal, char startCap, char endCap)
    {
        var bar = ScrollBar.Build(horizontal, length: 10, total: 100, viewportSpan: 10, offset: 0);
        Assert.AreEqual(startCap, bar[0]);
        Assert.AreEqual(endCap, bar[^1]);
    }

    [TestMethod]
    public void Build_AtStart_ThumbTouchesStartCap()
    {
        // offset 0 → thumb sits at the first track cell, right after the start cap.
        var bar = ScrollBar.Build(horizontal: false, length: 10, total: 100, viewportSpan: 10, offset: 0);
        Assert.AreEqual("▲█░░░░░░░▼", bar);
    }

    [TestMethod]
    public void Build_AtEnd_ThumbTouchesEndCap()
    {
        // offset at maximum (total - viewportSpan) → thumb sits at the last track cell.
        var bar = ScrollBar.Build(horizontal: false, length: 10, total: 100, viewportSpan: 10, offset: 90);
        Assert.AreEqual("▲░░░░░░░█▼", bar);
    }

    [TestMethod]
    public void Build_ThumbSizeProportionalToViewport()
    {
        // Half the content visible → thumb spans roughly half the 8-cell track.
        var bar = ScrollBar.Build(horizontal: false, length: 10, total: 20, viewportSpan: 10, offset: 0);
        int thumb = bar.Count(c => c == '█');
        Assert.AreEqual(4, thumb);
    }

    [TestMethod]
    public void Build_ThumbNeverSmallerThanOneCell()
    {
        // Tiny viewport relative to a huge total still yields a visible 1-cell thumb.
        var bar = ScrollBar.Build(horizontal: false, length: 12, total: 100_000, viewportSpan: 1, offset: 0);
        Assert.IsTrue(bar.Contains('█'));
        Assert.AreEqual(1, bar.Count(c => c == '█'));
    }

    [TestMethod]
    public void Build_MidScroll_ThumbBetweenCaps()
    {
        var bar = ScrollBar.Build(horizontal: false, length: 12, total: 100, viewportSpan: 10, offset: 45);
        int firstThumb = bar.IndexOf('█');
        int lastThumb = bar.LastIndexOf('█');
        Assert.IsTrue(firstThumb > 0, "thumb should not overlap the start cap");
        Assert.IsTrue(lastThumb < bar.Length - 1, "thumb should not overlap the end cap");
    }
}
