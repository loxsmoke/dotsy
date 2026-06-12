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

    public FakeSseHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _body   = body;
        _status = status;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var resp = new HttpResponseMessage(_status)
        {
            Content = new StringContent(_body, Encoding.UTF8, "text/event-stream")
        };
        return Task.FromResult(resp);
    }
}
