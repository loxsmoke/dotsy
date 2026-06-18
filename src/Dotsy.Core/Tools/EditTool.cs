using System.Text.Json;
using System.Text;
using Dotsy.Core.Utils;
using Dotsy.Core.Tools.Interfaces;

namespace Dotsy.Core.Tools;

public sealed class EditTool : ITool
{
    public const string ToolName = "Edit";
    public string Name => ToolName;
    public string Description => "Edit a file by replacing an exact 1-based inclusive line range. Read the file first, then provide path, start_line, end_line, and new_string.";
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
        var path = MakeRelative(input.GetStringPropertyOrEmpty("path"), cwd);
        var newString = input.GetStringPropertyOrEmpty("new_string");
        int added;
        int deleted;

        if (input.TryGetProperty("start_line", out var sl) && input.TryGetProperty("end_line", out var el))
        {
            deleted = el.GetInt32() - sl.GetInt32() + 1;
            added = CountLines(newString);
        }
        else
        {
            var oldString = input.GetStringPropertyOrEmpty("old_string");
            deleted = CountLines(oldString);
            added = CountLines(newString);
        }

        return path + LineDelta(added, deleted);
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
        var path = ReadTool.ResolvePath(input, ctx.Cwd);
        var newString = input.GetProperty("new_string").GetString() ?? "";

        if (!File.Exists(path))
            return Task.FromResult(ToolResult.Err($"File not found: {path}"));

        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) { return Task.FromResult(ToolResult.Err(ex.Message)); }

        bool hasStartLine = input.TryGetProperty("start_line", out var startEl);
        bool hasEndLine   = input.TryGetProperty("end_line",   out var endEl);

        if (hasStartLine || hasEndLine)
        {
            if (!hasStartLine || !hasEndLine)
                return Task.FromResult(ToolResult.Err("start_line and end_line must both be provided"));

            var result = ApplyLineRange(text, startEl.GetInt32(), endEl.GetInt32(), newString);
            if (result.IsError) return Task.FromResult(result);
            text = result.Content;
        }
        else
        {
            var oldString = input.GetStringPropertyOrEmpty("old_string");
            bool replaceAll = input.TryGetProperty("replace_all", out var ra) && ra.GetBoolean();

            var result = ApplyTextReplace(text, oldString, newString, replaceAll);
            if (result.IsError) return Task.FromResult(result);
            text = result.Content;
        }

        try
        {
            File.WriteAllText(path, text);
            return Task.FromResult(ToolResult.Ok($"Edited: {path}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Err(ex.Message));
        }
    }

    internal static ToolResult ApplyLineRange(string text, int startLine, int endLine, string newString)
    {
        var lines = text.Split('\n');
        int count = lines.Length;

        // Trim phantom empty line that Split produces when text ends with \n
        bool trailingNewline = text.EndsWith('\n');
        int logicalCount = trailingNewline ? count - 1 : count;

        if (startLine < 1 || endLine < startLine || startLine > logicalCount)
            return ToolResult.Err($"Line range {startLine}-{endLine} is out of bounds (file has {logicalCount} lines)");

        endLine = Math.Min(endLine, logicalCount);

        var before = lines[..(startLine - 1)];
        // after includes the trailing empty string when file ends with \n, preserving it
        var after  = lines[endLine..];

        var replacement = newString.TrimEnd('\n').Split('\n');

        return ToolResult.Ok(string.Join('\n', before.Concat(replacement).Concat(after)));
    }

    internal static ToolResult ApplyTextReplace(string text, string oldString, string newString, bool replaceAll)
    {
        if (!replaceAll)
        {
            int first = text.IndexOf(oldString, StringComparison.Ordinal);
            if (first == -1)
                return ToolResult.Err("old_string not found in file");

            int second = text.IndexOf(oldString, first + 1, StringComparison.Ordinal);
            if (second != -1)
                return ToolResult.Err("old_string is not unique — use replace_all: true to replace all occurrences");

            return ToolResult.Ok(text[..first] + newString + text[(first + oldString.Length)..]);
        }
        else
        {
            if (!text.Contains(oldString, StringComparison.Ordinal))
                return ToolResult.Err("old_string not found in file");

            return ToolResult.Ok(text.Replace(oldString, newString, StringComparison.Ordinal));
        }
    }

    private static string Snippet(string text, int maxLen)
    {
        var first = text.Split('\n')[0].Trim();
        return first.Length <= maxLen ? first : first[..maxLen] + "...";
    }

    private static string LineDelta(int added, int deleted)
    {
        if (added == 0 && deleted == 0) return "";
        var parts = new List<string>();
        if (added > 0) parts.Add($"+{added}");
        if (deleted > 0) parts.Add($"-{deleted}");
        return "  " + string.Join(" ", parts) + " lines";
    }

    private static int CountLines(string text) =>
        string.IsNullOrEmpty(text) ? 0 : text.TrimEnd('\n').Split('\n').Length;

    private static string MakeRelative(string path, string cwd)
    {
        if (string.IsNullOrEmpty(path)) return ".";
        try
        {
            var abs = Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(cwd, path));
            var cwdFull = Path.GetFullPath(cwd);
            if (abs.StartsWith(cwdFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return abs[(cwdFull.Length + 1)..];
            if (abs.Equals(cwdFull, StringComparison.OrdinalIgnoreCase))
                return ".";
        }
        catch { }
        return path;
    }

    internal static void AppendEditInput(StringBuilder sb, JsonElement input, string header, string cwd)
    {
        sb.AppendLine(header);
        sb.AppendLine($"path: {MakeRelative(input.GetStringPropertyOrEmpty("path"), cwd)}");

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
