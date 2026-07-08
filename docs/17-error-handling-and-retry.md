## 17. Error Handling and Retry

### 17.1 Rate Limits (HTTP 429)

Exponential back-off with jitter, implemented in the provider layer so all providers share the same policy:

- **Base delay:** 1 000 ms
- **Multiplier:** 2.0 per attempt
- **Cap:** 30 000 ms
- **Jitter:** ±20% on each calculated delay (**goose** approach — only agent with real jitter, prevents thundering herds)
- **Retry-After honour:** If the provider returns a `Retry-After` or `x-ratelimit-reset` header, that value replaces the calculated delay (**cline** / **continue** pattern)
- **Max retries:** 10 (**pi** pattern; `RetryPolicy.MaxRetries`, an init-only property, not currently wired to an env var or config key)

Retries are applied by `RetryingProvider`, which wraps every resolved `IProvider`. The TUI status bar shows "retrying in Ns · attempt x/10" during waits.

```csharp
public class RetryPolicy
{
    public int MaxRetries { get; init; } = 10;
    public int BaseDelayMs { get; init; } = 1_000;
    public int MaxDelayMs { get; init; } = 30_000;
    public double Multiplier { get; init; } = 2.0;
    public double JitterFactor { get; init; } = 0.2;   // ±20%

    public TimeSpan NextDelay(int attempt, TimeSpan? serverHint = null)
    {
        if (serverHint.HasValue) return serverHint.Value;
        var ms = BaseDelayMs * Math.Pow(Multiplier, attempt);
        ms = Math.Min(ms, MaxDelayMs);
        var jitter = ms * JitterFactor * (Random.Shared.NextDouble() * 2 - 1);
        return TimeSpan.FromMilliseconds(ms + jitter);
    }
}
```

### 17.2 Transient Network and 5xx Errors

All provider implementations map HTTP responses to a typed `ProviderError` hierarchy:

```csharp
public abstract record ProviderError;
public record RateLimitError(TimeSpan? RetryAfter)        : ProviderError;  // retried
public record ServerError(int StatusCode)                 : ProviderError;  // 5xx — retried
public record NetworkError(Exception Inner)               : ProviderError;  // ECONNRESET etc. — retried
public record AuthError(string Message)                   : ProviderError;  // 401/403 — not retried
public record RequestError(int StatusCode, string Detail = "") : ProviderError;  // other 4xx — not retried
public record ContextLengthError()                        : ProviderError;  // handled by §11
public record ModelUnknownError(string Message)           : ProviderError;  // unknown model id — not retried
```

`RetryPolicy.ShouldRetry` retries only `RateLimitError`, `ServerError`, and `NetworkError`; every
other error type propagates immediately (wrapped in a `ProviderException`).

`ServerError` and `NetworkError` are always retried up to `MaxRetries`. A known gap in **pi** (connection-level errors bypass retry) is avoided here by catching both `HttpRequestException` and `SocketException` and mapping them to `NetworkError`.

### 17.3 Partial Stream Failures

If the SSE stream is cut mid-response (connection dropped before `[DONE]`), the provider layer raises `NetworkError`. The agent loop discards the partial response, does not append it to message history, and retries the full request (counts against `MaxRetries`). A "Connection lost — retrying…" notice is emitted to the TUI.

### 17.4 Authentication Errors

Auth errors (401/403) are not retried — they fail immediately with a descriptive message in the conversation panel explaining the likely cause and the config key to check. This is the approach of every agent surveyed; retrying auth errors wastes quota and delays the user from fixing the actual problem.

---

