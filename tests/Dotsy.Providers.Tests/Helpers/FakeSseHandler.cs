using System.Net;
using System.Text;

namespace Dotsy.Providers.Tests.Helpers;

/// <summary>
/// HttpMessageHandler that returns a pre-canned SSE body for all requests.
/// </summary>
internal sealed class FakeSseHandler : HttpMessageHandler
{
    private readonly string _body;
    private readonly HttpStatusCode _status;

    /// <summary>The serialized request body of the most recent call (null until a request is sent).</summary>
    public string? LastRequestBody { get; private set; }

    /// <summary>The absolute URI of the most recent call (null until a request is sent).</summary>
    public Uri? LastRequestUri { get; private set; }

    public FakeSseHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _body   = body;
        _status = status;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        LastRequestUri = request.RequestUri;
        if (request.Content is not null)
            LastRequestBody = await request.Content.ReadAsStringAsync(ct);

        var resp = new HttpResponseMessage(_status)
        {
            Content = new StringContent(_body, Encoding.UTF8, "text/event-stream")
        };
        return resp;
    }
}
