using System.Runtime.CompilerServices;
using Dotsy.Core.Providers;

namespace Dotsy.Core.Tests.Helpers;

/// <summary>
/// Deterministic IProvider for unit tests. Supply one or more event sequences;
/// each StreamAsync call dequeues the next sequence (repeats the last when exhausted).
/// </summary>
internal sealed class FakeProvider : IProvider
{
    private readonly IReadOnlyList<ProviderEvent>[] _responses;
    private int _callCount;
    private readonly List<ChatRequest> _requests = [];

    public string Name => "fake";
    public int CallCount => _callCount;
    public IReadOnlyList<ChatRequest> Requests => _requests;

    public FakeProvider(params IReadOnlyList<ProviderEvent>[] responses)
    {
        if (responses.Length == 0)
            _responses = [Array.Empty<ProviderEvent>()];
        else
            _responses = responses;
    }

    public Task<ModelInfo> GetModelInfoAsync(string modelId, CancellationToken ct)
        => Task.FromResult(new ModelInfo(modelId, 200_000, 8_192));

    public async IAsyncEnumerable<ProviderEvent> StreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        _requests.Add(request);
        var idx = Math.Min(_callCount++, _responses.Length - 1);
        foreach (var ev in _responses[idx])
        {
            ct.ThrowIfCancellationRequested();
            yield return ev;
        }
        await Task.CompletedTask;
    }
}
