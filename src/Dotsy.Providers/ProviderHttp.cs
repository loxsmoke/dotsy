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
            // ResponseHeadersRead: return as soon as headers arrive instead of buffering the
            // whole body, so SSE responses stream incrementally rather than in one burst.
            var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
            return new PostResult(
                await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct), null);
        }
        catch (Exception ex)
        {
            var error = new StreamError(new ProviderException(new NetworkError(ex)));
            return new PostResult(null, error);
        }
    }

    // Reads the next SSE line, converting mid-stream transport failures into a StreamError.
    // With ResponseHeadersRead the connection stays open while the body streams, so a server
    // dying mid-generation surfaces here rather than in PostAsync; without this guard the
    // IOException escapes the async iterator and crashes the loop. Cancellation still throws.
    public static async Task<(string? Line, StreamError? Error)> ReadSseLineAsync(
        StreamReader reader,
        CancellationToken ct)
    {
        try
        {
            return (await reader.ReadLineAsync(ct), null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, new StreamError(new ProviderException(new NetworkError(ex))));
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
