using Dotsy.Core.Providers;

namespace Dotsy.Core.Loop;

/// <summary>
/// Configuration and timing logic for exponential-backoff retries.
/// Consumed by <see cref="Dotsy.Providers.RetryingProvider"/>, which wraps any
/// <c>IProvider</c> and uses this policy on every failed stream attempt.
/// <para>
/// <see cref="ShouldRetry"/> classifies a <see cref="Dotsy.Core.Providers.ProviderError"/>
/// as retriable (<see cref="Dotsy.Core.Providers.RateLimitError"/>,
/// <see cref="Dotsy.Core.Providers.ServerError"/>, or
/// <see cref="Dotsy.Core.Providers.NetworkError"/>); all other error types propagate
/// immediately without retrying.
/// </para>
/// <para>
/// <see cref="NextDelay"/> computes how long to wait before the next attempt using
/// exponential backoff (<c>BaseDelayMs * Multiplier^attempt</c>) capped at
/// <c>MaxDelayMs</c>, with random jitter applied to spread load across concurrent
/// clients. If the server supplies a retry-after hint (e.g. from a 429 response),
/// that value is used as-is and the backoff formula is bypassed.
/// </para>
/// </summary>
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
