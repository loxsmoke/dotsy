using Dotsy.Core.Providers;

namespace Dotsy.Providers;

public static class ProviderHttp
{
    public static async Task<PostResult> PostAsync(
        HttpClient http,
        string endpoint,
        HttpContent content,
        CancellationToken ct)
    {
        try
        {
            return new PostResult(await http.PostAsync(endpoint, content, ct), null);
        }
        catch (Exception ex)
        {
            var error = new StreamError(new ProviderException(new NetworkError(ex)));
            return new PostResult(null, error);
        }
    }

    public static bool TryClassifyCommonError(
        HttpResponseMessage response,
        out ProviderError? error)
    {
        var status = (int)response.StatusCode;
        if (status == 429)
        {
            error = new RateLimitError(ParseRetryAfter(response));
            return true;
        }

        if (status >= 500)
        {
            error = new ServerError(status);
            return true;
        }

        error = null;
        return false;
    }

    private static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Retry-After", out var values))
            return null;

        return int.TryParse(values.FirstOrDefault(), out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : null;
    }

    public readonly record struct PostResult(
        HttpResponseMessage? Response,
        StreamError? Error);
}
