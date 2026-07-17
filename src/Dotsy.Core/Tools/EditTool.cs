using System.Text.Json;
using System.Text;
using Dotsy.Core.Utils;
using Dotsy.Core.Tools.Interfaces;

namespace Dotsy.Core.Tools;

public sealed class EditTool : ITool
{
    public const string ToolName = "Edit";
    public string Name => ToolName;
    public string Description => "Edit a file by replacing an exact 1-based inclusive line range (start_line/end_line + new_string) or an exact text match (old_string + new_string). The range is REPLACED by new_string — to insert without deleting anything, include the range's existing line(s) in new_string. Read the file first. The result echoes the edited region with current line numbers; check it before editing further.";
    public JsonElement InputSchema => ToolSchemas.EditSchema;
    public ToolSafety Safety => ToolSafety.Sequential;
    public bool IsCompletionSignal => false;
    public bool IsWriteTool => true;

    public string FormatRunApproval(JsonElement input, string cwd)
    {
        var path = input.GetStringPropertyOrEmpty("path");
        var newString = input.GetStringPropertyOrEmpty("new_string");

        var fileName = Path.GetFileName(path);
        if (input.TryGetProperty("start_line", out var startLine) && input.TryGetProperty("end_line", out var endLine))
            return $"{fileName}  lines {startLine.GetInt32()}-{endLine.GetInt32()} -> \"{Snippet(newString, 30)}\"";

        var oldString = input.GetStringPropertyOrEmpty("old_string");
        return $"{fileName}  \"{Snippet(oldString, 30)}\" -> \"{Snippet(newString, 30)}\"";
    }

    public string FormatPanelArgument(JsonElement input, string cwd)
    {
        var path = PathDisplay.MakeRelative(input.GetStringPropertyOrEmpty("path"), cwd);
        var newString = input.GetStringPropertyOrEmpty("new_string");
        int added;
        int deleted;

        if (input.TryGetProperty("start_line", out var sl) && input.TryGetProperty("end_line", out var el))
        {
            deleted = el.GetInt32() - sl.GetInt32() + 1;
            added = EditSummaryFormatter.CountLines(newString);
        }
        else
        {
            var oldString = input.GetStringPropertyOrEmpty("old_string");
            deleted = EditSummaryFormatter.CountLines(oldString);
            added = EditSummaryFormatter.CountLines(newString);
        }

        return path + EditSummaryFormatter.LineDelta(added, deleted);
    }

    public string? FormatPanelResult(JsonElement input, string resultContent, string cwd)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(resultContent))
        {
            sb.AppendLine("Output");
            sb.AppendLine(resultContent.TrimEnd());
            sb.AppendLine();
        }
        AppendEditInput(sb, input, "Input", cwd);
        return sb.ToString().TrimEnd();
    }

    public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.GetStringPropertyOrEmpty("path")))
            return Task.FromResult(ToolResult.Err("Edit requires a 'path' argument"));
        var path = ReadTool.ResolvePath(input, ctx.Cwd);

        if (!input.TryGetProperty("new_string", out var newEl) || newEl.ValueKind != JsonValueKind.String)
            return Task.FromResult(ToolResult.Err("Edit requires a 'new_string' argument (use an empty string to clear the range)"));
        var newString = newEl.GetString() ?? "";

        if (!File.Exists(path))
            return Task.FromResult(ToolResult.Err($"File not found: {path}"));

        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) { return Task.FromResult(ToolResult.Err(ex.Message)); }

        bool hasStartLine = input.TryGetProperty("start_line", out _);
        bool hasEndLine   = input.TryGetProperty("end_line",   out _);

        var regions = new List<(int Start, int End)>();
        int occurrences = 0;

        if (hasStartLine || hasEndLine)
        {
            // GetFlexInt tolerates numbers sent as strings, the same leniency Read has.
            int? startLine = ReadTool.GetFlexInt(input, "start_line");
            int? endLine   = ReadTool.GetFlexInt(input, "end_line");
            if (startLine is null || endLine is null)
                return Task.FromResult(ToolResult.Err("start_line and end_line must both be provided as numbers"));

            var result = ApplyLineRange(text, startLine.Value, endLine.Value, newString, out var region);
            if (result.IsError) return Task.FromResult(result);
            text = result.Content;
            regions.Add(region);
        }
        else
        {
            var oldString = input.GetStringPropertyOrEmpty("old_string");
            bool replaceAll = input.TryGetProperty("replace_all", out var ra) &&
                (ra.ValueKind == JsonValueKind.True ||
                 (ra.ValueKind == JsonValueKind.String && bool.TryParse(ra.GetString(), out var b) && b));

            var result = ApplyTextReplace(text, oldString, newString, replaceAll, out var region, out occurrences);
            if (result.IsError) return Task.FromResult(result);
            text = result.Content;
            if (region is { } r) regions.Add(r);
        }

        try
        {
            File.WriteAllText(path, text);
            return Task.FromResult(ToolResult.Ok(BuildEditResult($"Edited: {path}", text, regions, occurrences)));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Err(ex.Message));
        }
    }

    /// <param name="region">The replaced range in the coordinates of the returned text.</param>
    internal static ToolResult ApplyLineRange(string text, int startLine, int endLine, string newString, out (int Start, int End) region)
    {
        region = default;
        var lines = text.Split('\n');
        int count = lines.Length;

        // Trim phantom empty line that Split produces when text ends with \n
        bool trailingNewline = text.EndsWith('\n');
        int logicalCount = trailingNewline ? count - 1 : count;

        // Report each failure distinctly: a lumped "out of bounds" message sends the model
        // chasing the wrong problem (e.g. an inverted range looks nothing like a length issue).
        if (startLine < 1)
            return ToolResult.Err($"start_line must be at least 1 (got {startLine})");
        if (endLine < startLine)
            return ToolResult.Err($"end_line ({endLine}) is before start_line ({startLine}) — for a single line use end_line = start_line");
        if (startLine > logicalCount)
            return ToolResult.Err($"start_line {startLine} exceeds file length ({logicalCount} lines)");

        endLine = Math.Min(endLine, logicalCount);

        var before = lines[..(startLine - 1)];
        // after includes the trailing empty string when file ends with \n, preserving it
        var after  = lines[endLine..];

        // Replacement lines arrive '\n'-separated; match the file's dominant EOL so editing a
        // CRLF file doesn't leave it with mixed line endings.
        bool crlf = text.Contains("\r\n", StringComparison.Ordinal);
        var replacement = newString.TrimEnd('\n').Split('\n');
        for (int i = 0; i < replacement.Length; i++)
        {
            var line = replacement[i].TrimEnd('\r');
            replacement[i] = crlf ? line + "\r" : line;
        }

        region = (startLine, startLine + replacement.Length - 1);

        var joined = string.Join('\n', before.Concat(replacement).Concat(after));
        // The CR appended to the last replacement line is stray when nothing follows it.
        if (crlf && joined.EndsWith('\r'))
            joined = joined[..^1];
        return ToolResult.Ok(joined);
    }

    /// <param name="region">The replaced range in the coordinates of the returned text;
    /// null when replace_all touched multiple places.</param>
    internal static ToolResult ApplyTextReplace(string text, string oldString, string newString, bool replaceAll,
        out (int Start, int End)? region, out int occurrences)
    {
        region = null;
        occurrences = 0;

        // EOL fallback: models often supply old_string with '\n' where the file has "\r\n" (or
        // the reverse). When the literal match fails, retry with the other convention and carry
        // new_string over to the same convention.
        if (oldString.Length > 0 && oldString.Contains('\n') &&
            !text.Contains(oldString, StringComparison.Ordinal))
        {
            var lf   = oldString.Replace("\r\n", "\n");
            var crlf = lf.Replace("\n", "\r\n");
            if (crlf != oldString && text.Contains(crlf, StringComparison.Ordinal))
            {
                oldString = crlf;
                newString = newString.Replace("\r\n", "\n").Replace("\n", "\r\n");
            }
            else if (lf != oldString && text.Contains(lf, StringComparison.Ordinal))
            {
                oldString = lf;
                newString = newString.Replace("\r\n", "\n");
            }
        }

        if (!replaceAll)
        {
            int first = text.IndexOf(oldString, StringComparison.Ordinal);
            if (first == -1)
                return ToolResult.Err("old_string not found in file");

            int second = text.IndexOf(oldString, first + 1, StringComparison.Ordinal);
            if (second != -1)
                return ToolResult.Err("old_string is not unique — use replace_all: true to replace all occurrences");

            occurrences = 1;
            int firstLine = 1;
            for (int i = 0; i < first; i++)
                if (text[i] == '\n') firstLine++;
            int newLineBreaks = 0;
            foreach (var c in newString)
                if (c == '\n') newLineBreaks++;
            region = (firstLine, firstLine + newLineBreaks);

            return ToolResult.Ok(text[..first] + newString + text[(first + oldString.Length)..]);
        }
        else
        {
            if (oldString.Length == 0 || !text.Contains(oldString, StringComparison.Ordinal))
                return ToolResult.Err("old_string not found in file");

            for (int at = text.IndexOf(oldString, StringComparison.Ordinal);
                 at != -1;
                 at = text.IndexOf(oldString, at + oldString.Length, StringComparison.Ordinal))
                occurrences++;

            return ToolResult.Ok(text.Replace(oldString, newString, StringComparison.Ordinal));
        }
    }

    internal static int LogicalLineCount(string text)
    {
        var count = text.Split('\n').Length;
        return text.EndsWith('\n') ? count - 1 : count;
    }

    // Assembles a write-tool success message: header line, then the edited region(s) of the final
    // text as numbered lines. The echo is what lets the model catch a bad edit immediately instead
    // of discovering it at the next build.
    internal static string BuildEditResult(string header, string text,
        IReadOnlyList<(int Start, int End)> regions, int occurrences = 0)
    {
        if (regions.Count > 0 &&
            RenderEditedRegions(text, regions) is { Length: > 0 } snippet)
        {
            return $"{header}\nFile is now {LogicalLineCount(text)} lines. Edited region with current line numbers — verify it is what you intended:\n{snippet}";
        }
        if (occurrences > 1)
            return $"{header}\nReplaced {occurrences} occurrences.";
        return header;
    }

    // Renders the given 1-based inclusive line ranges of text as numbered lines (the same format
    // Read produces, so the numbers can be reused directly), each padded with a few context lines;
    // overlapping windows are merged and separate windows are divided by "...".
    internal static string RenderEditedRegions(string text, IReadOnlyList<(int Start, int End)> regions,
        int context = 3, int maxLinesPerRegion = 40)
    {
        var lines = text.Split('\n');
        int logical = text.EndsWith('\n') ? lines.Length - 1 : lines.Length;
        if (logical <= 0 || regions.Count == 0) return "";

        var windows = new List<(int Start, int End)>();
        foreach (var r in regions.OrderBy(r => r.Start))
        {
            int s = Math.Clamp(r.Start - context, 1, logical);
            int e = Math.Clamp(r.End + context, 1, logical);
            if (e < s) continue;
            if (windows.Count > 0 && s <= windows[^1].End + 1)
                windows[^1] = (windows[^1].Start, Math.Max(windows[^1].End, e));
            else
                windows.Add((s, e));
        }
        if (windows.Count == 0) return "";

        int numWidth = windows[^1].End.ToString().Length;
        var sb = new StringBuilder();
        for (int w = 0; w < windows.Count; w++)
        {
            if (w > 0) sb.Append("...\n");
            var (s, e) = windows[w];
            int shown = 0;
            for (int ln = s; ln <= e; ln++)
            {
                if (++shown > maxLinesPerRegion)
                {
                    sb.Append($"<snip: lines {ln}-{e} not shown>\n");
                    break;
                }
                sb.Append(ln.ToString().PadLeft(numWidth)).Append(' ')
                  .Append(lines[ln - 1].TrimEnd('\r')).Append('\n');
            }
        }
        return sb.ToString().TrimEnd('\n');
    }

    private static string Snippet(string text, int maxLen)
    {
        var first = text.Split('\n')[0].Trim();
        return first.Length <= maxLen ? first : first[..maxLen] + "...";
    }

    internal static void AppendEditInput(StringBuilder sb, JsonElement input, string header, string cwd)
    {
        sb.AppendLine(header);
        sb.AppendLine($"path: {PathDisplay.MakeRelative(input.GetStringPropertyOrEmpty("path"), cwd)}");

        if (input.TryGetProperty("start_line", out var startLine) && input.TryGetProperty("end_line", out var endLine))
        {
            sb.AppendLine($"start_line: {startLine.GetInt32()}");
            sb.AppendLine($"end_line: {endLine.GetInt32()}");
        }
        else
        {
            sb.AppendLine("old_string:");
            sb.AppendLine(input.GetStringPropertyOrEmpty("old_string"));
        }

        sb.AppendLine("new_string:");
        sb.AppendLine(input.GetStringPropertyOrEmpty("new_string"));

        if (input.TryGetProperty("replace_all", out var replaceAll))
            sb.AppendLine($"replace_all: {replaceAll.GetBoolean()}");
    }
}
