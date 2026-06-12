using System.Text;
using System.Text.Json;

using Dotsy.Core.Utils;
using Dotsy.Core.Tools.Interfaces;
using Ivy.Ripgrep;

namespace Dotsy.Core.Tools;

public sealed class GrepTool : ITool
{
    private const int MaxMatches = 100;
    private const int MaxBytes = 51_200;
    private const int MaxLines = 2000;
    private const int MaxLineLen = 500;

    private readonly RipgrepClient _ripgrep = new();

    public string Name => "Grep";
    public string Description => "Search file contents using bundled ripgrep (rg). Respects .gitignore. Supports regex patterns.";
    public JsonElement InputSchema => ToolSchemas.GrepSchema;
    public ToolSafety Safety => ToolSafety.ReadOnly;
    public bool IsCompletionSignal => false;

    public string FormatPanelArgument(JsonElement input, string cwd)
    {
        var pattern = input.GetStringPropertyOrEmpty("pattern");
        var rawPath = input.GetStringPropertyOrEmpty("path");
        var rel = string.IsNullOrEmpty(rawPath) ? "." : ReadTool.MakeRelative(rawPath, cwd);
        return $"\"{pattern}\" in {rel}";
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var pattern = input.GetProperty("pattern").GetString() ?? "";
        var searchPath = input.TryGetProperty("path", out _)
            ? ReadTool.ResolvePath(input, ctx.Cwd)
            : ctx.Cwd;

        var contextLines = input.TryGetProperty("context_lines", out var cl) ? cl.GetInt32() : 0;
        var ignoreCase = input.TryGetProperty("ignore_case", out var ic) && ic.GetBoolean();

        var args = new List<string>
        {
            "--line-number",
            "--color=never"
        };

        if (ignoreCase) args.Add("--ignore-case");
        if (contextLines > 0) args.Add($"--context={contextLines}");

        args.Add($"--max-count={MaxMatches}");
        args.Add("--");
        args.Add(pattern);
        args.Add(searchPath);

        var result = await _ripgrep.RunRawAsync(args, ctx.Cwd, ct);

        if (string.IsNullOrEmpty(result.StandardOutput) && result.ExitCode == 1)
            return ToolResult.Ok("No matches found.");

        if (result.ExitCode != 0 && result.ExitCode != 1)
            return ToolResult.Err(string.IsNullOrWhiteSpace(result.StandardError)
                ? $"ripgrep exited with code {result.ExitCode}"
                : result.StandardError.Trim());

        return ToolResult.Ok(FormatOutput(result.StandardOutput));
    }

    private static string FormatOutput(string output)
    {
        var sb = new StringBuilder();
        var lineCount = 0;
        var truncated = false;

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0 && lineCount == 0)
                continue;

            if (line.Length > MaxLineLen)
                line = line[..MaxLineLen] + "...";

            sb.AppendLine(line);
            lineCount++;

            if (sb.Length > MaxBytes || lineCount >= MaxLines)
            {
                truncated = true;
                break;
            }
        }

        if (truncated)
            sb.AppendLine("<truncated: output exceeded limits>");

        return sb.ToString();
    }
}
