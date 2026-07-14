using Dotsy.Core.Loop.Data;
using Dotsy.Core.Providers;

namespace Dotsy.Core.Tests;

[TestClass]
public sealed class TokenSpeedTests
{
    [TestMethod]
    public void TokenUsageUpdated_ObservedRate_ComputedFromStreamDuration()
    {
        var ev = new TokenUsageUpdated(new UsageUpdate(100, 50, 0, 0), durationMs: 5000);

        Assert.AreEqual(10.0, ev.ObservedTokensPerSecond);
        Assert.IsNull(ev.ServerTokensPerSecond);
        Assert.AreEqual(10.0, ev.TokensPerSecond);
    }

    [TestMethod]
    public void TokenUsageUpdated_ServerRate_PreferredOverObserved()
    {
        // Server measured 2s of pure generation inside a 5s observed stream.
        var ev = new TokenUsageUpdated(
            new UsageUpdate(100, 50, 0, 0, ServerDurationMs: 2000), durationMs: 5000);

        Assert.AreEqual(10.0, ev.ObservedTokensPerSecond);
        Assert.AreEqual(25.0, ev.ServerTokensPerSecond);
        Assert.AreEqual(25.0, ev.TokensPerSecond);
    }

    [TestMethod]
    public void TokenUsageUpdated_NoTimingData_RatesAreNull()
    {
        var ev = new TokenUsageUpdated(new UsageUpdate(100, 50, 0, 0));

        Assert.IsNull(ev.ObservedTokensPerSecond);
        Assert.IsNull(ev.ServerTokensPerSecond);
        Assert.IsNull(ev.TokensPerSecond);
    }

    [TestMethod]
    public void TokenUsageTracker_AggregatesRateAcrossCalls_PreferringServerDuration()
    {
        var tracker = new TokenUsageTracker();
        tracker.RecordUsage(new UsageUpdate(10, 30, 0, 0, ServerDurationMs: 1000), observedDurationMs: 4000);
        tracker.RecordUsage(new UsageUpdate(10, 30, 0, 0), observedDurationMs: 2000);

        Assert.AreEqual(60, tracker.TotalOutputTokens);
        Assert.AreEqual(3000, tracker.TotalGenerationMs);
        Assert.AreEqual(20.0, tracker.OutputTokensPerSecond);
    }

    [TestMethod]
    public void TokenUsageTracker_NoTimingData_RateIsNull()
    {
        var tracker = new TokenUsageTracker();
        tracker.RecordUsage(new UsageUpdate(10, 30, 0, 0));

        Assert.IsNull(tracker.OutputTokensPerSecond);
    }
}
