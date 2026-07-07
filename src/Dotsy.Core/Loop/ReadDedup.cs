using System.Collections.Generic;
using Dotsy.Core.Loop.Data;
using Dotsy.Core.Providers;

namespace Dotsy.Core.Loop;

/// <summary>
/// De-duplicates repeated file reads. A weaker model often re-reads the same file many times in a
/// session (observed: one 30-line file read 15× in a single run). Re-injecting the whole file each
/// time wastes turns and bloats context, which in turn triggers more summarization — a feedback loop.
///
/// The de-dupe is <b>compaction-safe</b>: it only replaces a re-read with a short stub when the
/// earlier read's content is <i>still present verbatim</i> in the live message history. Once the
/// summarizer/compaction has evicted that content, the full file is returned again so the model
/// never loses information it may need. Reads of a different line range, or of a file that changed
/// on disk, are never de-duped.
/// </summary>
public static class ReadDedup
{
    /// <summary>
    /// Returns a stub string to use instead of re-reading, or null if the read should proceed
    /// normally (cache miss, file changed, different range, or content no longer in context).
    /// </summary>
    public static string? StubForRepeatRead(
        IReadOnlyDictionary<string, ReadCacheEntry> cache,
        IReadOnlyList<Message> messages,
        string resolvedPath,
        long mtimeTicks,
        long size,
        int offset,
        int limit)
    {
        if (!cache.TryGetValue(resolvedPath, out var entry))
            return null;                                   // never read this file
        if (entry.MTimeTicks != mtimeTicks || entry.Size != size)
            return null;                                   // changed on disk -> must re-read
        // The requested line range must be fully covered by the range already read, so the lines
        // are actually present in the earlier output. A wider (or offset) request must re-read.
        // (limit is a line count; [offset, offset+limit) is the covered half-open range.)
        if (offset < entry.Offset || (long)offset + limit > (long)entry.Offset + entry.Limit)
            return null;                                   // not fully covered -> must re-read
        if (!ContentStillLive(messages, entry.Content))
            return null;                                   // evicted by compaction -> must re-read

        return $"[already read] These lines are within a copy of this file you already read this "
             + "session (unchanged since), still in the conversation above — re-reading was skipped "
             + "to save context. Refer to that earlier output. Read again only for a range outside "
             + "what you read, or use FindDefs to jump to a symbol.";
    }

    /// <summary>True if <paramref name="content"/> is still present verbatim in a live tool result.</summary>
    public static bool ContentStillLive(IReadOnlyList<Message> messages, string content)
    {
        // Summarization replaces old tool-result blocks with a "[tool summary] ..." text block and
        // removes the original, so an exact match means the content has not yet been compacted out.
        foreach (var message in messages)
        {
            if (message is not UserMessage user)
                continue;
            foreach (var block in user.Content)
            {
                if (block is ToolResultBlock tr && string.Equals(tr.Content, content, System.StringComparison.Ordinal))
                    return true;
            }
        }
        return false;
    }
}
