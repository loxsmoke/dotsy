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
    private readonly IProvider _inner;
    private readonly RetryPolicy _policy;
    private readonly Func<RetryScheduled, CancellationToken, Task>? _onRetry;

    public string Name => _inner.Name;

    public RetryingProvider(
        IProvider inner,
        RetryPolicy? policy = null,
        Func<RetryScheduled, CancellationToken, Task>? onRetry = null)
    {
        _inner = inner;
        _policy = policy ?? new RetryPolicy();
        _onRetry = onRetry;
    }

    public Task<ModelInfo> GetModelInfoAsync(string modelId, CancellationToken ct) =>
        _inner.GetModelInfoAsync(modelId, ct);

    public Task<IReadOnlyList<ModelInfo>> GetModelsAsync(CancellationToken ct) =>
        _inner.GetModelsAsync(ct);

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

            await foreach (var ev in _inner.StreamAsync(request, ct))
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

            if (attempt >= _policy.MaxRetries)
            {
                yield return new StreamError(new ProviderException(retryableError));
                yield break;
            }

            var delay = _policy.NextDelay(attempt, serverHint);
            int delaySecs = Math.Max(1, (int)delay.TotalSeconds);
            attempt++;

            if (_onRetry is not null)
            {
                var notification = new RetryScheduled(attempt, _policy.MaxRetries, delaySecs);
                await _onRetry(notification, ct);
            }

            await Task.Delay(delay, ct);
        }
    }
}
