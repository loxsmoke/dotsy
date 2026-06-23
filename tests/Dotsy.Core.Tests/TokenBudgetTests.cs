using Dotsy.Core.Loop;
using Dotsy.Core.Loop.Data;

namespace Dotsy.Core.Tests;

[TestClass]
public sealed class TokenBudgetTests
{
    // ── ShouldCompact ─────────────────────────────────────────────────────────

    [TestMethod]
    public void ShouldCompact_FalseWhenBelowThreshold()
    {
        // usable = 100_000 - 10_000 = 90_000; used = 50_000 < 90_000
        var b = new TokenBudget(100_000, 10_000, 20_000, 50_000);
        Assert.IsFalse(b.ShouldCompact);
    }

    [TestMethod]
    public void ShouldCompact_TrueWhenUsedExceedsUsable()
    {
        // usable = 100_000 - 10_000 = 90_000; used = 95_000 > 90_000
        var b = new TokenBudget(100_000, 10_000, 20_000, 95_000);
        Assert.IsTrue(b.ShouldCompact);
    }

    // ── ShouldWarn ────────────────────────────────────────────────────────────

    [TestMethod]
    public void ShouldWarn_FalseAt59Percent()
    {
        // 59_000 / 100_000 = 0.59 < 0.60
        var b = new TokenBudget(100_000, 0, 0, 59_000);
        Assert.IsFalse(b.ShouldWarn);
    }

    [TestMethod]
    public void ShouldWarn_TrueAt60Percent()
    {
        // 60_000 / 100_000 = 0.60 >= 0.60
        var b = new TokenBudget(100_000, 0, 0, 60_000);
        Assert.IsTrue(b.ShouldWarn);
    }

    // ── WithUsed ──────────────────────────────────────────────────────────────

    [TestMethod]
    public void WithUsed_ReturnsNewRecordWithUpdatedCount()
    {
        var original = new TokenBudget(200_000, 16_384, 20_000, 0);
        var updated  = original.WithUsed(50_000);

        Assert.AreEqual(50_000, updated.UsedTokens);
        Assert.AreEqual(0,      original.UsedTokens, "Original must be immutable");
    }

    // ── UsagePct ──────────────────────────────────────────────────────────────

    [TestMethod]
    public void UsagePct_CalculatesCorrectly()
    {
        var b = new TokenBudget(200_000, 0, 0, 100_000);
        Assert.AreEqual(0.5f, b.UsagePct, 0.001f);
    }
}
