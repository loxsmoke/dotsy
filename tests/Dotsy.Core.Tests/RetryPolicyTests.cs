using Dotsy.Core.Loop;
using Dotsy.Core.Providers;

namespace Dotsy.Core.Tests;

[TestClass]
public sealed class RetryPolicyTests
{
    // ── Jitter stays within ±20% of the exponential base ─────────────────────

    [TestMethod]
    public void NextDelay_JitterStaysWithinBounds()
    {
        var policy = new RetryPolicy
        {
            BaseDelayMs = 1_000,
            Multiplier  = 2.0,
            MaxDelayMs  = 30_000,
            JitterFactor = 0.2
        };

        // attempt=0 → exponential = 1000 * 2^0 = 1000 ms
        double expected = 1_000;
        double lower = expected * (1 - 0.2);
        double upper = expected * (1 + 0.2);

        for (int i = 0; i < 500; i++)
        {
            var delay = policy.NextDelay(0).TotalMilliseconds;
            Assert.IsTrue(delay >= lower && delay <= upper,
                $"Jitter out of range on iteration {i}: {delay:F1} not in [{lower},{upper}]");
        }
    }

    // ── Server hint overrides calculation ────────────────────────────────────

    [TestMethod]
    public void NextDelay_ServerHintOverrides()
    {
        var policy = new RetryPolicy { BaseDelayMs = 1_000, Multiplier = 2.0, MaxDelayMs = 30_000 };
        var hint   = TimeSpan.FromSeconds(42);

        var delay = policy.NextDelay(5, serverHint: hint);

        Assert.AreEqual(hint, delay);
    }

    // ── Caps at MaxDelayMs ────────────────────────────────────────────────────

    [TestMethod]
    public void NextDelay_CapsAtMaxDelayMs()
    {
        var policy = new RetryPolicy
        {
            BaseDelayMs  = 1_000,
            Multiplier   = 2.0,
            MaxDelayMs   = 5_000,
            JitterFactor = 0.0   // disable jitter so cap is exact
        };

        // attempt=10 → 1000 * 2^10 = 1_024_000 ms >> 5000 ms cap
        var delay = policy.NextDelay(10).TotalMilliseconds;

        Assert.IsTrue(delay <= 5_000,
            $"Expected delay ≤ 5000 ms, got {delay:F1}");
    }

    // ── ShouldRetry classification ────────────────────────────────────────────

    [TestMethod]
    public void ShouldRetry_TrueForRetriableErrors()
    {
        Assert.IsTrue(RetryPolicy.ShouldRetry(new RateLimitError(null)));
        Assert.IsTrue(RetryPolicy.ShouldRetry(new ServerError(500)));
        Assert.IsTrue(RetryPolicy.ShouldRetry(new NetworkError(new Exception())));
    }

    [TestMethod]
    public void ShouldRetry_FalseForNonRetriableErrors()
    {
        Assert.IsFalse(RetryPolicy.ShouldRetry(new AuthError("Unauthorized")));
        Assert.IsFalse(RetryPolicy.ShouldRetry(new RequestError(400)));
        Assert.IsFalse(RetryPolicy.ShouldRetry(new ContextLengthError()));
    }
}
