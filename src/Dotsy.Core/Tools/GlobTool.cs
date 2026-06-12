using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Dotsy.Core.Utils;
using Dotsy.Core.Tools.Interfaces;

namespace Dotsy.Core.Tools;

public sealed class GlobTool : ITool
{
    public string Name => "Glob";
    public string Description => "Find files matching a glob pattern. Results sorted by last write time (newest first).";
    public JsonElement InputSchema => ToolSchemas.GlobSchema;
    public ToolSafety Safety => ToolSafety.ReadOnly;
    public bool IsCompletionSignal => false;

    public string FormatPanelArgument(JsonElement input, string cwd) =>
        input.GetStringPropertyOrEmpty("pattern");

    public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var pattern = input.GetProperty("pattern").GetString() ?? "";
        var searchRoot = input.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String
            ? Path.IsPathRooted(p.GetString()!) ? p.GetString()! : Path.GetFullPath(Path.Combine(ctx.Cwd, p.GetString()!))
            : ctx.Cwd;

        if (!Directory.Exists(searchRoot))
            return Task.FromResult(ToolResult.Err($"Directory not found: {searchRoot}"));

        try
        {
            var matcher = CreateMatcher(NormalizeGlob(pattern));
            var files = Directory.EnumerateFiles(searchRoot, "*", SearchOption.AllDirectories)
                .Where(f => matcher(Path.GetRelativePath(searchRoot, f)))
                .OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc)
                .ToList();

            if (files.Count == 0)
                return Task.FromResult(ToolResult.Ok("No files matched."));

            var sb = new StringBuilder();
            foreach (var f in files)
                sb.AppendLine(Path.GetRelativePath(searchRoot, f));

            return Task.FromResult(ToolResult.Ok(sb.ToString()));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Err(ex.Message));
        }
    }

    private static string NormalizeGlob(string pattern) =>
        pattern.Replace('\\', '/').TrimStart('/');

    private static Func<string, bool> CreateMatcher(string pattern)
    {
        bool matchFileNameOnly = !pattern.Contains('/', StringComparison.Ordinal);
        var regex = new Regex("^" + GlobToRegex(pattern) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return path =>
        {
            var candidate = path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
            if (matchFileNameOnly)
                candidate = Path.GetFileName(candidate);
            return regex.IsMatch(candidate);
        };
    }

    private static string GlobToRegex(string pattern)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < pattern.Length; i++)
        {
            var ch = pattern[i];
            if (ch == '*')
            {
                bool doubleStar = i + 1 < pattern.Length && pattern[i + 1] == '*';
                if (doubleStar)
                {
                    i++;
                    if (i + 1 < pattern.Length && pattern[i + 1] == '/')
                    {
                        i++;
                        sb.Append("(?:.*/)?");
                    }
                    else
                    {
                        sb.Append(".*");
                    }
                }
                else
                {
                    sb.Append("[^/]*");
                }
            }
            else if (ch == '?')
            {
                sb.Append("[^/]");
            }
            else
            {
                sb.Append(Regex.Escape(ch.ToString()));
            }
        }

        return sb.ToString();
    }
}
