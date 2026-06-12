namespace Dotsy.Core.Providers;

/// <summary>
/// Represents a provider that can interact with AI language models to process chat requests.
/// </summary>
public interface IProvider
{
    /// <summary>
    /// Gets the name of the provider.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Retrieves information about a specific model.
    /// </summary>
    /// <param name="modelId">The identifier of the model to retrieve information for.</param>
    /// <param name="ct">The cancellation token to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the model information.</returns>
    Task<ModelInfo> GetModelInfoAsync(string modelId, CancellationToken ct);

    /// <summary>
    /// Streams provider events in response to a chat request.
    /// </summary>
    /// <param name="request">The chat request containing the conversation context and parameters.</param>
    /// <param name="ct">The cancellation token to cancel the streaming operation.</param>
    /// <returns>An asynchronous enumerable of provider events generated during the streaming response.</returns>
    IAsyncEnumerable<ProviderEvent> StreamAsync(ChatRequest request, CancellationToken ct);
}
