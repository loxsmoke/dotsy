using System.Text.Json;
using System.Text;
using Dotsy.Core.Utils;
using Dotsy.Core.Tools.Interfaces;

namespace Dotsy.Core.Tools;

public sealed class MultiEditTool : ITool
{
    public string Name => "MultiEdit";
    public string Description => "Apply multiple non-overlapping edits to a file in one call. Each edit replaces an exact 1-based inclusive start_line/end_line range.";
    public JsonElement InputSchema => ToolSchemas.MultiEditSchema;
    public ToolSafety Safety => ToolSafety.Sequential;
    public bool IsCompletionSignal => false;
    public bool IsWriteTool => true;

    public string FormatRunApproval(JsonElement input, string cwd)
    {
        var path = input.GetStringPropertyOrEmpty("path");
        var fileName = Path.GetFileName(path);
        var count = input.TryGetProperty("edits", out var e) && e.ValueKind == JsonValueKind.Array
            ? e.GetArrayLength()
            : 0;
        return $"{fileName}  {count} edit{(count == 1 ? "" : "s")}";
    }

    public string FormatPanelArgument(JsonElement input, string cwd)
    {
        var path = MakeRelative(input.GetStringPropertyOrEmpty("path"), cwd);
        var count = input.TryGetProperty("edits", out var editsEl) && editsEl.ValueKind == JsonValueKind.Array
            ? editsEl.GetArrayLength()
            : 0;

        int totalAdded = 0;
        int totalDeleted = 0;
        if (input.TryGetProperty("edits", out var edits))
        {
            foreach (var edit in edits.EnumerateArray())
            {
                var newString = edit.GetStringPropertyOrEmpty("new_string");
                if (edit.TryGetProperty("start_line", out var sl) && edit.TryGetProperty("end_line", out var el))
                {
                    totalDeleted += el.GetInt32() - sl.GetInt32() + 1;
                    totalAdded += CountLines(newString);
                }
                else
                {
                    var oldString = edit.GetStringPropertyOrEmpty("old_string");
                    totalDeleted += CountLines(oldString);
                    totalAdded += CountLines(newString);
                }
            }
        }

        return $"{path}  {count} edit{(count == 1 ? "" : "s")}{LineDelta(totalAdded, totalDeleted)}";
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

        sb.AppendLine("Input");
        sb.AppendLine($"path: {MakeRelative(input.GetStringPropertyOrEmpty("path"), cwd)}");

        if (input.TryGetProperty("edits", out var edits) && edits.ValueKind == JsonValueKind.Array)
        {
            var index = 1;
            foreach (var edit in edits.EnumerateArray())
            {
                sb.AppendLine();
                EditTool.AppendEditInput(sb, edit, $"Edit {index}", cwd);
                index++;
            }
        }

        return sb.ToString().TrimEnd();
    }

    public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var path = ReadTool.ResolvePath(input, ctx.Cwd);

        if (!File.Exists(path))
            return Task.FromResult(ToolResult.Err($"File not found: {path}"));

        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) { return Task.FromResult(ToolResult.Err(ex.Message)); }

        if (!input.TryGetProperty("edits", out var editsEl))
            return Task.FromResult(ToolResult.Err("edits array is required"));

        int applied = 0;
        foreach (var edit in editsEl.EnumerateArray())
        {
            var newString = edit.GetProperty("new_string").GetString() ?? "";
            bool hasStartLine = edit.TryGetProperty("start_line", out var startEl);
            bool hasEndLine   = edit.TryGetProperty("end_line",   out var endEl);

            ToolResult result;
            if (hasStartLine || hasEndLine)
            {
                if (!hasStartLine || !hasEndLine)
                    return Task.FromResult(ToolResult.Err($"Edit {applied + 1}: start_line and end_line must both be provided"));

                result = EditTool.ApplyLineRange(text, startEl.GetInt32(), endEl.GetInt32(), newString);
            }
            else
            {
                var oldString = edit.GetStringPropertyOrEmpty("old_string");
                bool replaceAll = edit.TryGetProperty("replace_all", out var ra) && ra.GetBoolean();
                result = EditTool.ApplyTextReplace(text, oldString, newString, replaceAll);
            }

            if (result.IsError)
                return Task.FromResult(ToolResult.Err($"Edit {applied + 1}: {result.Content}"));

            text = result.Content;
            applied++;
        }

        try
        {
            File.WriteAllText(path, text);
            return Task.FromResult(ToolResult.Ok($"Applied {applied} edit(s) to: {path}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Err(ex.Message));
        }
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
}
