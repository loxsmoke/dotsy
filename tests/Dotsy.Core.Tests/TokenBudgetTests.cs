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
        var b = new TokenBudget(100_000, 10_000, 20_000, 50_000, 1.0f);
        Assert.IsFalse(b.ShouldCompact);
    }

    [TestMethod]
    public void ShouldCompact_TrueWhenUsedExceedsUsable()
    {
        // usable = 100_000 - 10_000 = 90_000; used = 95_000 > 90_000
        var b = new TokenBudget(100_000, 10_000, 20_000, 95_000, 1.0f);
        Assert.IsTrue(b.ShouldCompact);
    }

    // ── ShouldWarn ────────────────────────────────────────────────────────────

    [TestMethod]
    public void ShouldWarn_FalseAt59Percent()
    {
        // 59_000 / 100_000 = 0.59 < 0.60
        var b = new TokenBudget(100_000, 0, 0, 59_000, 0.9f);
        Assert.IsFalse(b.ShouldWarn);
    }

    [TestMethod]
    public void ShouldWarn_TrueAt60Percent()
    {
        // 60_000 / 100_000 = 0.60 >= 0.60
        var b = new TokenBudget(100_000, 0, 0, 60_000, 0.9f);
        Assert.IsTrue(b.ShouldWarn);
    }

    // ── WithUsed ──────────────────────────────────────────────────────────────

    [TestMethod]
    public void WithUsed_ReturnsNewRecordWithUpdatedCount()
    {
        var original = new TokenBudget(200_000, 16_384, 20_000, 0, 0.9f);
        var updated  = original.WithUsed(50_000);

        Assert.AreEqual(50_000, updated.UsedTokens);
        Assert.AreEqual(0,      original.UsedTokens, "Original must be immutable");
    }

    // ── UsagePct ──────────────────────────────────────────────────────────────

    [TestMethod]
    public void UsagePct_CalculatesCorrectly()
    {
        var b = new TokenBudget(200_000, 0, 0, 100_000, 0.9f);
        Assert.AreEqual(0.5f, b.UsagePct, 0.001f);
    }

    // ── UsablePct (what the compaction trigger measures) ──────────────────────

    [TestMethod]
    public void UsablePct_MeasuresAgainstUsableBudgetNotFullWindow()
    {
        // Reproduces the session that died with ContextTooSmall: 64k window, 16k reserve.
        var b = new TokenBudget(65_536, 16_384, 20_000, 49_263, 0.9f);
        // Against the full window it looks like only ~75% used, below a 0.80 trigger...
        Assert.IsTrue(b.UsagePct < 0.80f, $"UsagePct was {b.UsagePct}");
        // ...but the usable budget (49_152) is already exhausted, so compaction must fire.
        Assert.IsTrue(b.UsablePct >= 0.80f, $"UsablePct was {b.UsablePct}");
        Assert.IsTrue(b.UsablePct >= 1.0f);
    }

    [TestMethod]
    public void UsablePct_ZeroWhenNoUsableBudget()
    {
        var b = new TokenBudget(10_000, 10_000, 0, 5_000, 0.9f); // usable = 0
        Assert.AreEqual(0f, b.UsablePct, 0.001f);
    }
}
