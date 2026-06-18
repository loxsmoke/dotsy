using System.Text.Json;
using Dotsy.Core.Utils;
using Dotsy.Core.Tools.Interfaces;

namespace Dotsy.Core.Tools;

public sealed class WriteTool : ITool
{
    public const string ToolName = "Write";
    public string Name => ToolName;
    public string Description => "Write or overwrite a file. Creates parent directories if missing.";
    public JsonElement InputSchema => ToolSchemas.WriteSchema;
    public ToolSafety Safety => ToolSafety.Destructive;
    public bool IsCompletionSignal => false;
    public bool IsWriteTool => true;

    public string FormatRunApproval(JsonElement input, string cwd)
    {
        var path = input.GetStringPropertyOrEmpty("path");
        var content = input.GetStringPropertyOrEmpty("content");

        var fileName = Path.GetFileName(path);
        var newLines = CountLines(content);

        var fullPath = Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(cwd, path));
        if (File.Exists(fullPath))
        {
            var oldLines = CountLines(File.ReadAllText(fullPath));
            return $"{fileName}  {newLines} lines  (was {oldLines})";
        }

        return $"{fileName}  +{newLines} lines  (new file)";
    }

    public string FormatPanelArgument(JsonElement input, string cwd)
    {
        var path = MakeRelative(input.GetStringPropertyOrEmpty("path"), cwd);
        var content = input.GetStringPropertyOrEmpty("content");
        return $"{path}  +{CountPanelLines(content)} lines";
    }

    public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var path = ReadTool.ResolvePath(input, ctx.Cwd);
        var content = input.GetProperty("content").GetString() ?? "";

        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(path, content);
            return Task.FromResult(ToolResult.Ok($"Written: {path}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Err(ex.Message));
        }
    }

    private static int CountLines(string text) =>
        string.IsNullOrEmpty(text) ? 0 : text.Split('\n').Length;

    private static int CountPanelLines(string text) =>
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
