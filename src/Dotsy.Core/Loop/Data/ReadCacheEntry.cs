namespace Dotsy.Core.Loop.Data;

/// <summary>
/// Records the outcome of a file Read so a later identical Read can be de-duplicated while the
/// content is still live in context. Keyed by resolved absolute path in <see cref="LoopContext.ReadCache"/>.
/// </summary>
/// <param name="MTimeTicks">File last-write time (UTC ticks) when it was read; a change invalidates the entry.</param>
/// <param name="Size">File length in bytes when it was read; a change invalidates the entry.</param>
/// <param name="Offset">The 0-based line offset that was requested.</param>
/// <param name="Limit">The line count that was requested.</param>
/// <param name="Content">The exact tool-result text that was returned (used to detect it's still in context).</param>
public sealed record ReadCacheEntry(long MTimeTicks, long Size, int Offset, int Limit, string Content);
