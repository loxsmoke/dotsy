namespace Dotsy.Core.Providers;

public abstract record ProviderError;

public record RateLimitError(TimeSpan? RetryAfter) : ProviderError;

public record ServerError(int StatusCode) : ProviderError;

public record NetworkError(Exception Inner) : ProviderError;

public record AuthError(string Message) : ProviderError;

public record RequestError(int StatusCode, string Detail = "") : ProviderError;

public record ContextLengthError() : ProviderError;

public sealed class ProviderException(ProviderError error) : Exception(FormatMessage(error))
{
    public ProviderError Error { get; } = error;

    private static string FormatMessage(ProviderError error) => error switch
    {
        AuthError ae        => $"AuthError: {ae.Message}",
        RateLimitError rl   => rl.RetryAfter.HasValue
                                 ? $"RateLimitError: retry after {rl.RetryAfter.Value.TotalSeconds}s"
                                 : "RateLimitError: rate limit exceeded",
        ServerError se      => $"ServerError: HTTP {se.StatusCode}",
        NetworkError ne     => $"NetworkError: {ne.Inner.Message}",
        ContextLengthError  => "ContextLengthError: context window exceeded",
        RequestError re     => string.IsNullOrEmpty(re.Detail)
                                 ? $"RequestError: HTTP {re.StatusCode}"
                                 : $"RequestError: HTTP {re.StatusCode}: {re.Detail}",
        _                   => error.ToString()
    };
}
