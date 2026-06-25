using System.Runtime.CompilerServices;
using Dotsy.Core.Loop;
using Dotsy.Core.Loop.Data;
using Dotsy.Core.Providers;

namespace Dotsy.Providers.Tests;

[TestClass]
public sealed class RetryingProviderTests
{
    [TestMethod]
    public async Task StreamAsync_RetriesRetryableErrorAndYieldsSuccessfulAttempt()
    {
        var inner = new ScriptedProvider(
            [new TextDelta("partial"), new StreamError(new ProviderException(new ServerError(500)))],
            [new TextDelta("final"), new StreamEnd(StopReason.EndTurn)]);
        var retries = new List<RetryScheduled>();
        var provider = new RetryingProvider(
            inner,
            ZeroDelayPolicy(maxRetries: 2),
            (retry, _) =>
            {
                retries.Add(retry);
                return Task.CompletedTask;
            });

        var events = await Collect(provider.StreamAsync(Request(), CancellationToken.None));

        Assert.AreEqual(2, inner.StreamCalls);
        CollectionAssert.AreEqual(new[] { "final" }, events.OfType<TextDelta>().Select(e => e.Text).ToArray());
        Assert.AreEqual(StopReason.EndTurn, events.OfType<StreamEnd>().Single().Reason);
        Assert.AreEqual(1, retries.Count);
        Assert.AreEqual(1, retries[0].AttemptNumber);
        Assert.AreEqual(2, retries[0].MaxAttempts);
    }

    [TestMethod]
    public async Task StreamAsync_UsesRetryAfterHintWhenPresent()
    {
        var inner = new ScriptedProvider(
            [new StreamError(new ProviderException(new RateLimitError(TimeSpan.FromSeconds(7))))],
            [new StreamEnd(StopReason.EndTurn)]);
        var retries = new List<RetryScheduled>();
        var provider = new RetryingProvider(
            inner,
            ZeroDelayPolicy(maxRetries: 1),
            (retry, _) =>
            {
                retries.Add(retry);
                return Task.CompletedTask;
            });

        var events = await Collect(provider.StreamAsync(Request(), CancellationToken.None));

        Assert.AreEqual(2, inner.StreamCalls);
        Assert.AreEqual(StopReason.EndTurn, events.OfType<StreamEnd>().Single().Reason);
        Assert.AreEqual(7, retries.Single().DelaySeconds);
    }

    [TestMethod]
    public async Task StreamAsync_StopsAfterMaxRetriesAndYieldsProviderException()
    {
        var inner = new ScriptedProvider(
            [new StreamError(new ProviderException(new ServerError(500)))],
            [new StreamError(new ProviderException(new ServerError(503)))]);
        var provider = new RetryingProvider(inner, ZeroDelayPolicy(maxRetries: 1));

        var events = await Collect(provider.StreamAsync(Request(), CancellationToken.None));

        Assert.AreEqual(2, inner.StreamCalls);
        var error = events.OfType<StreamError>().Single();
        Assert.IsInstanceOfType<ProviderException>(error.Ex);
        Assert.IsInstanceOfType<ServerError>(((ProviderException)error.Ex).Error);
    }

    [TestMethod]
    public async Task StreamAsync_DoesNotRetryNonRetryableProviderError()
    {
        var inner = new ScriptedProvider(
            [new TextDelta("before"), new StreamError(new ProviderException(new AuthError("bad key")))]);
        var provider = new RetryingProvider(inner, ZeroDelayPolicy(maxRetries: 3));

        var events = await Collect(provider.StreamAsync(Request(), CancellationToken.None));

        Assert.AreEqual(1, inner.StreamCalls);
        CollectionAssert.AreEqual(new[] { "before" }, events.OfType<TextDelta>().Select(e => e.Text).ToArray());
        Assert.IsInstanceOfType<AuthError>(((ProviderException)events.OfType<StreamError>().Single().Ex).Error);
    }

    [TestMethod]
    public async Task GetModelsAsync_DelegatesToInnerProvider()
    {
        var inner = new ScriptedProvider();
        var provider = new RetryingProvider(inner);

        var models = await provider.GetModelsAsync(CancellationToken.None);
        var info = await provider.GetModelInfoAsync("fake", CancellationToken.None);

        Assert.AreEqual("scripted", provider.Name);
        Assert.AreEqual(1, models.Count);
        Assert.AreEqual("fake", models[0].Id);
        Assert.AreEqual("fake", info.Id);
        Assert.AreEqual(1, inner.GetModelsCalls);
        Assert.AreEqual(1, inner.GetModelInfoCalls);
    }

    private static RetryPolicy ZeroDelayPolicy(int maxRetries = 1) => new()
    {
        MaxRetries = maxRetries,
        BaseDelayMs = 0,
        MaxDelayMs = 0,
        JitterFactor = 0
    };

    private static ChatRequest Request() =>
        new("fake", "sys", [new UserMessage([new TextBlock("hi")])], [], 128);

    private static async Task<List<ProviderEvent>> Collect(IAsyncEnumerable<ProviderEvent> source)
    {
        var events = new List<ProviderEvent>();
        await foreach (var ev in source)
            events.Add(ev);
        return events;
    }

    private sealed class ScriptedProvider(params ProviderEvent[][] attempts) : IProvider
    {
        private readonly Queue<ProviderEvent[]> attempts = new(attempts);

        public string Name => "scripted";
        public int StreamCalls { get; private set; }
        public int GetModelsCalls { get; private set; }
        public int GetModelInfoCalls { get; private set; }

        public Task<ModelInfo> GetModelInfoAsync(string modelId, CancellationToken ct)
        {
            GetModelInfoCalls++;
            return Task.FromResult(new ModelInfo(modelId, 10, 5));
        }

        public Task<IReadOnlyList<ModelInfo>> GetModelsAsync(CancellationToken ct)
        {
            GetModelsCalls++;
            return Task.FromResult<IReadOnlyList<ModelInfo>>([new ModelInfo("fake", 10, 5)]);
        }

        public async IAsyncEnumerable<ProviderEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            StreamCalls++;
            foreach (var ev in attempts.Count > 0 ? attempts.Dequeue() : [])
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return ev;
            }
        }
    }
}
