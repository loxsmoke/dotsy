using System.Runtime.CompilerServices;
using Dotsy.Core.Loop;
using Dotsy.Core.Loop.Data;
using Dotsy.Core.Providers;

namespace Dotsy.Providers;

/// <summary>
/// Wraps any IProvider with exponential-backoff retry on retriable errors.
/// On each back-off the onRetry callback fires once per second so the caller
/// can emit RetryScheduled LoopEvents for the TUI countdown.
/// </summary>
public sealed class RetryingProvider : IProvider
{
    private readonly IProvider provider;
    private readonly RetryPolicy retryPolicy;
    private readonly Func<RetryScheduled, CancellationToken, Task>? onRetry;

    public string Name => provider.Name;

    public RetryingProvider(
        IProvider inner,
        RetryPolicy? policy = null,
        Func<RetryScheduled, CancellationToken, Task>? onRetry = null)
    {
        provider = inner;
        retryPolicy = policy ?? new RetryPolicy();
        this.onRetry = onRetry;
    }

    public Task<ModelInfo> GetModelInfoAsync(string modelId, CancellationToken ct) =>
        provider.GetModelInfoAsync(modelId, ct);

    public Task<IReadOnlyList<ModelInfo>> GetModelsAsync(CancellationToken ct) =>
        provider.GetModelsAsync(ct);

    public async IAsyncEnumerable<ProviderEvent> StreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        int attempt = 0;
        while (true)
        {
            var events = new List<ProviderEvent>();
            ProviderError? retryableError = null;
            TimeSpan? serverHint = null;

            await foreach (var ev in provider.StreamAsync(request, ct))
            {
                if (ev is StreamError se && se.Ex is ProviderException pe && RetryPolicy.ShouldRetry(pe.Error))
                {
                    retryableError = pe.Error;
                    if (pe.Error is RateLimitError rle)
                        serverHint = rle.RetryAfter;
                    // Discard any partial response — do not yield it
                    events.Clear();
                    break;
                }

                events.Add(ev);
            }

            if (retryableError is null)
            {
                foreach (var ev in events)
                    yield return ev;
                yield break;
            }

            if (attempt >= retryPolicy.MaxRetries)
            {
                yield return new StreamError(new ProviderException(retryableError));
                yield break;
            }

            var delay = retryPolicy.NextDelay(attempt, serverHint);
            int delaySecs = Math.Max(1, (int)delay.TotalSeconds);
            attempt++;

            if (onRetry is not null)
            {
                var notification = new RetryScheduled(attempt, retryPolicy.MaxRetries, delaySecs);
                await onRetry(notification, ct);
            }

            await Task.Delay(delay, ct);
        }
    }
}
