using System.Text;
using System.Text.Json;

using Dotsy.Core.Utils;
using Dotsy.Core.Tools.Interfaces;

namespace Dotsy.Core.Tools;

public sealed class GlobTool : ITool
{
    // Listed paths are capped so a huge match set doesn't flood the context; the exact total is
    // always reported regardless, so "how many" questions get a precise number.
    private const int MaxListed = 300;

    // Build/VCS directories that are noise for almost every query, skipped unless explicitly matched.
    private static readonly HashSet<string> NoiseDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "node_modules", ".vs", ".idea"
    };

    public string Name => "Glob";
    public string Description =>
        "Find files matching a glob pattern. Reports the total match count and lists results "
        + "(newest first). Skips build/VCS dirs (.git, bin, obj, node_modules) by default; use "
        + "exclude to skip more. Prefer this over a shell command for finding or counting files.";
    public JsonElement InputSchema => ToolSchemas.GlobSchema;
    public ToolSafety Safety => ToolSafety.ReadOnly;
    public bool IsCompletionSignal => false;

    public string FormatPanelArgument(JsonElement input, string cwd)
    {
        var pattern = input.GetStringPropertyOrEmpty("pattern");
        var exclude = input.GetStringPropertyOrEmpty("exclude");
        return string.IsNullOrEmpty(exclude) ? pattern : $"{pattern} (!{exclude})";
    }

    public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var pattern = input.GetProperty("pattern").GetString() ?? "";
        var exclude = input.GetStringPropertyOrEmpty("exclude");
        var searchRoot = input.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String
            ? Path.IsPathRooted(p.GetString()!) ? p.GetString()! : Path.GetFullPath(Path.Combine(ctx.Cwd, p.GetString()!))
            : ctx.Cwd;

        if (!Directory.Exists(searchRoot))
            return Task.FromResult(ToolResult.Err($"Directory not found: {searchRoot}"));

        try
        {
            var matcher = GlobMatcher.Create(pattern);
            var excludeMatcher = string.IsNullOrEmpty(exclude) ? null : GlobMatcher.Create(exclude);

            var matches = EnumerateFiles(searchRoot, exclude, excludeMatcher, ct)
                .Where(rel => matcher(rel))
                .ToList();

            if (matches.Count == 0)
                return Task.FromResult(ToolResult.Ok("0 files matched."));

            matches.Sort((a, b) =>
                File.GetLastWriteTimeUtc(Path.Combine(searchRoot, b))
                    .CompareTo(File.GetLastWriteTimeUtc(Path.Combine(searchRoot, a))));

            var sb = new StringBuilder();
            sb.AppendLine($"{matches.Count} file(s) matched.");
            sb.AppendLine();
            foreach (var rel in matches.Take(MaxListed))
                sb.AppendLine(rel);
            if (matches.Count > MaxListed)
                sb.AppendLine($"<showing first {MaxListed} of {matches.Count}>");

            return Task.FromResult(ToolResult.Ok(sb.ToString()));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Err(ex.Message));
        }
    }

    // Manual recursive walk so noise/excluded directories can be pruned (Directory.EnumerateFiles
    // with AllDirectories descends into everything). Yields paths relative to the search root.
    private static IEnumerable<string> EnumerateFiles(
        string root, string exclude, Func<string, bool>? excludeMatcher, CancellationToken ct)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            if (ct.IsCancellationRequested) yield break;
            var dir = stack.Pop();

            string[] subdirs;
            string[] files;
            try
            {
                subdirs = Directory.GetDirectories(dir);
                files = Directory.GetFiles(dir);
            }
            catch { continue; } // unreadable directory — skip

            foreach (var f in files)
            {
                var rel = Path.GetRelativePath(root, f);
                if (excludeMatcher is not null && excludeMatcher(rel)) continue;
                yield return rel;
            }

            foreach (var sd in subdirs)
            {
                var name = Path.GetFileName(sd);
                if (NoiseDirs.Contains(name)) continue;

                if (excludeMatcher is not null)
                {
                    var rel = Path.GetRelativePath(root, sd);
                    // Prune a directory by exact name (e.g. "extern") or glob match on its path.
                    if (name.Equals(exclude, StringComparison.OrdinalIgnoreCase)
                        || excludeMatcher(rel))
                        continue;
                }

                stack.Push(sd);
            }
        }
    }
}
