namespace Dotsy.Core.Providers;

/// <summary>Where a model's context-window / output limits came from.</summary>
public enum ModelInfoSource
{
    /// <summary>Reported live by the provider's API.</summary>
    Api,
    /// <summary>Looked up from the bundled ModelCatalog (no live source).</summary>
    Catalog,
    /// <summary>Generic fallback default (model unknown / lookup failed).</summary>
    Default
}

public record ModelInfo(string Id, int ContextWindow, int MaxOutputTokens, ModelInfoSource Source = ModelInfoSource.Default);
