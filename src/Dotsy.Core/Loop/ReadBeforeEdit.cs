using System.Text.Json;
using Dotsy.Core.Loop.Data;
using Dotsy.Core.Tools;

namespace Dotsy.Core.Loop;

/// <summary>
/// Enforces read-before-edit: an Edit/MultiEdit is rejected unless the agent has Read (or itself
/// written) the file this session and the file's on-disk state still matches that snapshot.
///
/// Rationale (observed dogfooding, webcam-sec session): a weaker model edits by line range from a
/// stale mental image of the file — after its own previous edit, after a whole-file Write, or with
/// no read at all — and silently corrupts brace structure, spiralling into build-fix-corrupt loops
/// ending in full-file rewrites. Line numbers carry no self-check, so the only safe anchor is a
/// fresh Read. The guard therefore has one extra rule for line-range edits: the agent's own
/// Edit/Write marks the file "written since last read", and a further line-range edit is rejected
/// until it Reads again. String-match (old_string) edits are exempt from that rule because a stale
/// image just produces a loud "old_string not found" instead of corruption.
///
/// Rejections happen before the user is asked to approve the edit, and the error text tells the
/// model the exact next action so it self-corrects in one round trip.
/// </summary>
public static class ReadBeforeEdit
{
    /// <summary>
    /// Returns a rejection for an Edit/MultiEdit of a file that was never read this session or
    /// whose on-disk state is stale, else null. Non-edit tools, missing paths, and missing files
    /// pass through so the tool itself reports its usual error.
    /// </summary>
    public static ToolResult? Check(LoopContext ctx, string cwd, string toolName, JsonElement input)
    {
        if (!string.Equals(toolName, EditTool.ToolName, StringComparison.Ordinal)
            && !string.Equals(toolName, MultiEditTool.ToolName, StringComparison.Ordinal))
            return null;

        var path = TryResolvePath(input, cwd);
        if (path is null)
            return null;

        long mtime, size;
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return null;                 // let the tool report "File not found"
            mtime = fi.LastWriteTimeUtc.Ticks;
            size = fi.Length;
        }
        catch { return null; }

        if (!ctx.FileFreshness.TryGetValue(path, out var entry))
            return ToolResult.Err(
                $"You must Read {path} before editing it. Read the file first, then retry the edit.");

        if (entry.MTimeTicks != mtime || entry.Size != size)
            return ToolResult.Err(
                $"{path} has changed on disk since you last read it, so your copy is stale. "
                + "Read the file again, then retry the edit.");

        if (!entry.ReadSinceLastWrite && IsLineRangeEdit(toolName, input))
            return ToolResult.Err(
                $"{path} was modified by your own earlier edit or write, so line numbers from your "
                + "last read are stale. Read the file again before editing it by line range.");

        return null;
    }

    /// <summary>Records a successful Read: the agent's image of the file matches disk.</summary>
    public static void RecordRead(LoopContext ctx, string cwd, JsonElement input) =>
        Record(ctx, cwd, input, readSinceLastWrite: true);

    /// <summary>
    /// Records a successful Edit/MultiEdit/Write by the agent: the file content is known but any
    /// line numbers from the last Read are stale until the file is read again.
    /// </summary>
    public static void RecordWrite(LoopContext ctx, string cwd, JsonElement input) =>
        Record(ctx, cwd, input, readSinceLastWrite: false);

    private static void Record(LoopContext ctx, string cwd, JsonElement input, bool readSinceLastWrite)
    {
        var path = TryResolvePath(input, cwd);
        if (path is null)
            return;
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return;                      // e.g. a Read of a directory
            ctx.FileFreshness[path] = new FileFreshnessEntry(
                fi.LastWriteTimeUtc.Ticks, fi.Length, readSinceLastWrite);
        }
        catch { /* freshness tracking is best-effort; the guard just asks for a re-read */ }
    }

    // True when the edit addresses the file by line numbers, which go stale after any write.
    // Unknown/unparseable shapes count as line-range (the conservative choice), matching the
    // advertised schemas where line ranges are the required form.
    private static bool IsLineRangeEdit(string toolName, JsonElement input)
    {
        if (string.Equals(toolName, EditTool.ToolName, StringComparison.Ordinal))
            return input.TryGetProperty("start_line", out _) || input.TryGetProperty("end_line", out _);

        // MultiEdit: line-range if any edit item uses line numbers. `edits` sent as a JSON-encoded
        // string (some models do this) can't be inspected cheaply here — treat as line-range.
        if (input.TryGetProperty("edits", out var edits) && edits.ValueKind == JsonValueKind.Array)
        {
            foreach (var edit in edits.EnumerateArray())
            {
                if (edit.ValueKind == JsonValueKind.Object
                    && (edit.TryGetProperty("start_line", out _) || edit.TryGetProperty("end_line", out _)))
                    return true;
                if (edit.ValueKind != JsonValueKind.Object)
                    return true;
            }
            return false;
        }
        return true;
    }

    // Non-throwing variant of ReadTool.ResolvePath using the same resolution rules, so freshness
    // keys match across Read, Edit, and Write inputs.
    private static string? TryResolvePath(JsonElement input, string cwd)
    {
        if (input.ValueKind != JsonValueKind.Object
            || !input.TryGetProperty("path", out var pathEl)
            || pathEl.ValueKind != JsonValueKind.String)
            return null;

        var path = pathEl.GetString();
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try { return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(cwd, path)); }
        catch { return null; }
    }
}
