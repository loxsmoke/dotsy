namespace Dotsy.Core.Providers;

public abstract record ProviderError;

public record RateLimitError(TimeSpan? RetryAfter) : ProviderError;

public record ServerError(int StatusCode) : ProviderError;

public record NetworkError(Exception Inner) : ProviderError;

public record AuthError(string Message) : ProviderError;

public record RequestError(int StatusCode, string Detail = "") : ProviderError;

public record ContextLengthError() : ProviderError;
public record ModelUnknownError(string Message) : ProviderError;

public sealed class ProviderException(ProviderError error) : Exception(FormatMessage(error))
{
    public ProviderError Error { get; } = error;

    private static string FormatMessage(ProviderError error) => error switch
    {
        AuthError ae        => $"Authentication failed: {ae.Message}",
        RateLimitError rl   => rl.RetryAfter.HasValue
                                 ? $"Rate limit exceeded — retry after {rl.RetryAfter.Value.TotalSeconds:0}s"
                                 : "Rate limit exceeded",
        ServerError se      => $"Provider server error (HTTP {se.StatusCode})",
        ContextLengthError  => "context window exceeded",
        ModelUnknownError mue  => $"Model unknown: {mue.Message}",
        RequestError re     => string.IsNullOrEmpty(re.Detail)
                                  ? $"Request rejected (HTTP {re.StatusCode})"
                                  : $"Request rejected (HTTP {re.StatusCode}): {re.Detail}",
        _                   => error.ToString()
    };
}
