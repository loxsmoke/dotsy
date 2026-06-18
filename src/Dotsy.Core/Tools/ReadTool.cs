using System.Text;
using System.Text.Json;
using System.Diagnostics;

using Dotsy.Core.Utils;
using Dotsy.Core.Tools.Interfaces;

namespace Dotsy.Core.Tools;

public sealed class ReadTool : ITool
{
    private const int MaxLines = 2000;
    private const int MaxBytes = 51_200;

    public const string ToolName = "Read";
    public string Name => ToolName;
    public string Description => "Read a file. Returns up to 2000 lines starting at offset. Binary files are rejected.";
    public JsonElement InputSchema => ToolSchemas.ReadSchema;
    public ToolSafety Safety => ToolSafety.ReadOnly;
    public bool IsCompletionSignal => false;

    public string FormatPanelArgument(JsonElement input, string cwd) =>
        MakeRelative(input.GetStringPropertyOrEmpty("path"), cwd);

    public string? FormatPanelResult(JsonElement input, string resultContent, string cwd)
    {
        if (string.IsNullOrEmpty(resultContent)) return null;
        var path = MakeRelative(input.GetStringPropertyOrEmpty("path"), cwd);
        var lines = resultContent.Split('\n').Count(l =>
        {
            var t = l.TrimStart();
            return t.Length > 0 && char.IsDigit(t[0]); // numbered content lines (not <truncated>/<diff>)
        });
        return $"{path}  {lines} lines";
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var path = ResolvePath(input, ctx.Cwd);

        // Accept either offset/limit (0-based offset + count) or start_line/end_line (1-based,
        // inclusive — the same parameter names Edit/MultiEdit use). Values may be numbers or
        // numeric strings, since models sometimes stringify them. offset/limit win if both given.
        int? startLine = GetFlexInt(input, "start_line");
        int? endLine   = GetFlexInt(input, "end_line");

        int offset = GetFlexInt(input, "offset")
            ?? (startLine.HasValue ? Math.Max(0, startLine.Value - 1) : 0);

        int limit = GetFlexInt(input, "limit")
            ?? (startLine.HasValue && endLine.HasValue
                ? Math.Max(1, endLine.Value - startLine.Value + 1)
                : MaxLines);

        var includeDiff = input.TryGetProperty("include_diff", out var d) && d.ValueKind == JsonValueKind.True;
        limit = Math.Min(limit, MaxLines);

        if (includeDiff && Directory.Exists(path))
        {
            var diffStat = await TryGetDiffStatAsync(path, ct);
            if (!string.IsNullOrWhiteSpace(diffStat))
                return ToolResult.Ok($"<git_diff_stat>\n{diffStat.TrimEnd()}\n</git_diff_stat>\n");
        }

        if (!File.Exists(path))
            return ToolResult.Err($"File not found: {path}");

        byte[] rawBytes;
        try { rawBytes = File.ReadAllBytes(path); }
        catch (Exception ex) { return ToolResult.Err(ex.Message); }

        // Binary detection
        for (int i = 0; i < Math.Min(rawBytes.Length, 8192); i++)
        {
            if (rawBytes[i] == 0)
                return ToolResult.Err($"Binary file: {path}");
        }

        if (rawBytes.Length > MaxBytes * 10)
        {
            // Large file: work with lines
        }

        var text = Encoding.UTF8.GetString(rawBytes);
        var lines = text.Split('\n');
        int totalLines = lines.Length;

        if (offset >= totalLines)
            return ToolResult.Err($"Offset {offset} exceeds file length ({totalLines} lines)");

        var slice = lines.Skip(offset).Take(limit).ToArray();
        bool truncated = offset + slice.Length < totalLines;

        var sb = new StringBuilder();
        // Right-align line numbers to the width of the largest number shown, then a single space,
        // so numbers form a clean column and don't stick to the content.
        int numWidth = (offset + slice.Length).ToString().Length;
        int lineNum = offset + 1;
        foreach (var line in slice)
        {
            sb.Append(lineNum++.ToString().PadLeft(numWidth));
            sb.Append(' ');
            sb.Append(line.TrimEnd('\r'));
            sb.Append('\n');
        }

        // Byte cap on output
        if (sb.Length > MaxBytes)
        {
            var result = sb.ToString();
            result = result[..MaxBytes];
            truncated = true;
            return ToolResult.Ok(result + $"\n<truncated: file has {totalLines} lines; use offset/limit to read more>");
        }

        if (truncated)
            sb.Append($"<truncated: {totalLines - offset - slice.Length} more lines; use offset={(offset + slice.Length)} to continue>\n");

        if (includeDiff)
        {
            var diffStat = await TryGetDiffStatAsync(path, ct);
            if (!string.IsNullOrWhiteSpace(diffStat))
            {
                sb.Append('\n');
                sb.Append("<git_diff_stat>\n");
                sb.Append(diffStat.Replace("\r\n", "\n").TrimEnd());
                sb.Append("\n</git_diff_stat>\n");
            }
        }

        return ToolResult.Ok(sb.ToString());
    }

    // Reads an integer property that may be a JSON number or a numeric string; null if absent/invalid.
    private static int? GetFlexInt(JsonElement input, string name)
    {
        if (!input.TryGetProperty(name, out var el)) return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n)) return n;
        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var s)) return s;
        return null;
    }

    internal static string ResolvePath(JsonElement input, string cwd)
    {
        var path = input.GetProperty("path").GetString() ?? "";
        return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(cwd, path));
    }

    internal static string MakeRelative(string path, string cwd)
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

    private static async Task<string?> TryGetDiffStatAsync(string path, CancellationToken ct)
    {
        if (!Directory.Exists(path))
            return null;

        var gitRoot = FindGitRoot(path);
        if (gitRoot is null || !PathsEqual(path, gitRoot))
            return null;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = gitRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            psi.ArgumentList.Add("diff");
            psi.ArgumentList.Add("--stat");

            using var proc = Process.Start(psi);
            if (proc is null)
                return null;

            var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
                return null;

            return stdout.Length <= MaxBytes ? stdout : stdout[..MaxBytes] + "\n<truncated: git diff stat exceeded output limit>";
        }
        catch
        {
            return null;
        }
    }

    private static string? FindGitRoot(string path)
    {
        var dir = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(dir))
        {
            if (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName ?? "";
        }
        return null;
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);
}
