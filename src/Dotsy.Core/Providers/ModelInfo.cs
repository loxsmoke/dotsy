namespace Dotsy.Core.Providers;

/// <summary>Where a model's context-window / output limits came from.</summary>
public enum ModelInfoSource
{
    /// <summary>Reported live by the provider's API (reflects the active runtime window).</summary>
    Api,
    /// <summary>The model's advertised capability ceiling, not the active runtime window
    /// (e.g. an Ollama model's architecture max while it's loaded with a smaller num_ctx).</summary>
    Advertised,
    /// <summary>Looked up from the bundled ModelCatalog (no live source).</summary>
    Catalog,
    /// <summary>Generic fallback default (model unknown / lookup failed).</summary>
    Default
}

public record ModelInfo(string Id, int ContextWindow, int MaxOutputTokens, ModelInfoSource Source = ModelInfoSource.Default);
