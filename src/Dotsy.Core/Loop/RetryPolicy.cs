using Dotsy.Core.Providers;

namespace Dotsy.Core.Loop;

public sealed class RetryPolicy
{
    public int MaxRetries { get; init; } = 10;
    public double BaseDelayMs { get; init; } = 1000;
    public double MaxDelayMs { get; init; } = 30_000;
    public double Multiplier { get; init; } = 2.0;
    public double JitterFactor { get; init; } = 0.2;

    private static readonly Random Rng = Random.Shared;

    public static bool ShouldRetry(ProviderError error) => error switch
    {
        RateLimitError => true,
        ServerError => true,
        NetworkError => true,
        _ => false
    };

    public TimeSpan NextDelay(int attempt, TimeSpan? serverHint = null)
    {
        if (serverHint.HasValue)
            return serverHint.Value;

        var exponential = BaseDelayMs * Math.Pow(Multiplier, attempt);
        var capped = Math.Min(exponential, MaxDelayMs);
        var jitter = capped * JitterFactor * (Rng.NextDouble() * 2 - 1);
        var ms = Math.Max(0, capped + jitter);
        return TimeSpan.FromMilliseconds(ms);
    }
}
