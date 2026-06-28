using System.Net;
using Dotsy.Core.Providers;

namespace Dotsy.Providers.Tests;

[TestClass]
public sealed class ProviderHttpTests
{
    [TestMethod]
    public async Task PostAsync_NetworkExceptionBecomesStreamError()
    {
        var expected = new HttpRequestException("offline");
        using var http = new HttpClient(new ThrowingHandler(expected))
        {
            BaseAddress = new Uri("https://example.test")
        };
        using var content = new StringContent("{}");

        var result = await ProviderHttp.PostAsync(
            http, "/messages", content, CancellationToken.None);

        Assert.IsNull(result.Response);
        var providerException = (ProviderException)result.Error!.Ex;
        var networkError = (NetworkError)providerException.Error;
        Assert.AreSame(expected, networkError.Inner);
    }

    [TestMethod]
    public void TryClassifyCommonError_RateLimitIncludesRetryAfter()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.Add("Retry-After", "12");

        var classified = ProviderHttp.TryClassifyCommonError(response, out var error);

        Assert.IsTrue(classified);
        Assert.AreEqual(TimeSpan.FromSeconds(12), ((RateLimitError)error!).RetryAfter);
    }

    [TestMethod]
    public void TryClassifyCommonError_ServerFailureIncludesStatus()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.BadGateway);

        var classified = ProviderHttp.TryClassifyCommonError(response, out var error);

        Assert.IsTrue(classified);
        Assert.AreEqual(502, ((ServerError)error!).StatusCode);
    }

    [TestMethod]
    public void TryClassifyCommonError_ClientFailureRemainsProviderSpecific()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.BadRequest);

        Assert.IsFalse(ProviderHttp.TryClassifyCommonError(response, out var error));
        Assert.IsNull(error);
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exception);
    }
}
