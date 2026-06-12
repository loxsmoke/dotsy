using System.Text;
using System.Text.Json;

using Dotsy.Core.Utils;
using Dotsy.Core.Tools.Interfaces;

namespace Dotsy.Core.Tools;

public sealed class ListTool : ITool
{
    public string Name => "List";
    public string Description => "List directory contents. Appends / to directories. Supports recursive listing.";
    public JsonElement InputSchema => ToolSchemas.ListSchema;
    public ToolSafety Safety => ToolSafety.ReadOnly;
    public bool IsCompletionSignal => false;

    public string FormatPanelArgument(JsonElement input, string cwd) =>
        ReadTool.MakeRelative(input.GetStringPropertyOrEmpty("path"), cwd);

    public string? FormatPanelResult(JsonElement input, string resultContent, string cwd)
    {
        if (string.IsNullOrEmpty(resultContent)) return null;
        var path = ReadTool.MakeRelative(input.GetStringPropertyOrEmpty("path"), cwd);
        if (resultContent.Trim() == "(empty)") return $"{path}  0 files";
        var count = resultContent.Split('\n').Count(l => l.Length > 0);
        return $"{path}  {count} files";
    }

    public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var path = ReadTool.ResolvePath(input, ctx.Cwd);
        bool recursive = input.TryGetProperty("recursive", out var r) && r.GetBoolean();

        if (!Directory.Exists(path))
            return Task.FromResult(ToolResult.Err($"Directory not found: {path}"));

        try
        {
            var option = recursive
                ? SearchOption.AllDirectories
                : SearchOption.TopDirectoryOnly;

            var entries = new List<string>();

            foreach (var dir in Directory.GetDirectories(path, "*", option))
                entries.Add(GetRelative(path, dir) + "/");

            foreach (var file in Directory.GetFiles(path, "*", option))
                entries.Add(GetRelative(path, file));

            entries.Sort(StringComparer.OrdinalIgnoreCase);

            var sb = new StringBuilder();
            foreach (var e in entries)
                sb.AppendLine(e);

            return Task.FromResult(ToolResult.Ok(sb.Length > 0 ? sb.ToString() : "(empty)"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Err(ex.Message));
        }
    }

    private static string GetRelative(string root, string full)
    {
        var rel = Path.GetRelativePath(root, full);
        return rel == "." ? "" : rel;
    }
}
