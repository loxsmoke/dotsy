namespace Dotsy.Core.Loop.Data;

/// <summary>
/// Records the last-known on-disk state of a file the agent has Read or written, used by
/// <see cref="Loop.ReadBeforeEdit"/> to enforce read-before-edit. Keyed by resolved absolute
/// path in <see cref="LoopContext.FileFreshness"/>. Always tracked, independent of
/// <see cref="Config.AgentConfig.DedupeReads"/>.
/// </summary>
/// <param name="MTimeTicks">File last-write time (UTC ticks) when last read/written; a mismatch with disk means the file changed outside the agent's view.</param>
/// <param name="Size">File length in bytes when last read/written; a change invalidates the entry.</param>
/// <param name="ReadSinceLastWrite">True when the latest snapshot came from a Read; false after the agent's own Edit/Write, meaning line numbers from the last Read are stale.</param>
public sealed record FileFreshnessEntry(long MTimeTicks, long Size, bool ReadSinceLastWrite);
