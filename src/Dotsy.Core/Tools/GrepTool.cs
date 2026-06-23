using System.Text;
using System.Text.Json;
using Dotsy.Core.Utils;
using Dotsy.Core.Tools.Interfaces;
using Ivy.Ripgrep;
using Microsoft.Extensions.Logging.Abstractions;
using Dotsy.Core.Loop.Data;

namespace Dotsy.Core.Tools;

public sealed class GrepTool : ITool
{
    private const int MaxMatches = 100;
    private const int MaxBytes = 51_200;
    private const int MaxLines = 2000;
    private const int MaxLineLen = 500;

    private RipgrepClient? _ripgrep;

    public const string ToolName = "Grep";
    public string Name => ToolName;
    public string Description => "Search file contents using bundled ripgrep (rg). Respects .gitignore. Supports regex patterns. Filter files with the glob parameter (e.g. \"*.cs\") and skip files/directories with the exclude parameter (e.g. \"terminal.gui\"); the path parameter is the directory to search, not a glob.";
    public JsonElement InputSchema => ToolSchemas.GrepSchema;
    public ToolSafety Safety => ToolSafety.ReadOnly;
    public bool IsCompletionSignal => false;

    public string FormatPanelArgument(JsonElement input, string cwd)
    {
        var pattern = input.GetStringPropertyOrEmpty("pattern");
        var rawPath = input.GetStringPropertyOrEmpty("path");
        var glob = input.GetStringPropertyOrEmpty("glob");

        // A glob mistakenly passed as path is shown as a filter, not a directory.
        if (LooksLikeGlob(rawPath) && string.IsNullOrEmpty(glob))
        {
            glob = rawPath;
            rawPath = "";
        }

        var exclude = input.GetStringPropertyOrEmpty("exclude");

        var rel = string.IsNullOrEmpty(rawPath) ? "." : ReadTool.MakeRelative(rawPath, cwd);
        var globPart = string.IsNullOrEmpty(glob) ? "" : $" ({glob})";
        var excludePart = string.IsNullOrEmpty(exclude) ? "" : $" (!{exclude.TrimStart('!')})";
        return $"\"{pattern}\" in {rel}{globPart}{excludePart}";
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var pattern = input.GetProperty("pattern").GetString() ?? "";

        var contextLines = input.TryGetProperty("context_lines", out var cl) ? cl.GetInt32() : 0;
        var ignoreCase = input.TryGetProperty("ignore_case", out var ic) && ic.GetBoolean();
        var glob = input.GetStringPropertyOrEmpty("glob");

        // Resolve the search root. Models frequently pass a glob such as "**/*.cs" in `path`;
        // ripgrep would treat that as a literal path, which Windows rejects (os error 123).
        // Detect that and reinterpret it as a --glob filter rooted at the working directory.
        var rawPath = input.GetStringPropertyOrEmpty("path");
        string searchPath;
        if (string.IsNullOrEmpty(rawPath))
        {
            searchPath = ctx.Cwd;
        }
        else if (LooksLikeGlob(rawPath))
        {
            if (string.IsNullOrEmpty(glob)) glob = rawPath;
            searchPath = ctx.Cwd;
        }
        else
        {
            searchPath = ReadTool.ResolvePath(input, ctx.Cwd);
            // A literal path must exist; rg otherwise fails with a raw "os error 2" the model
            // can't act on. Point it at the exclude parameter, the usual reason this happens.
            if (!Directory.Exists(searchPath) && !File.Exists(searchPath))
                return ToolResult.Err(
                    $"Search path not found: {ReadTool.MakeRelative(searchPath, ctx.Cwd)}\n" +
                    "The `path` argument is the directory or file to search IN, and it must exist. " +
                    "To skip a directory, leave `path` at the default and pass it in `exclude` instead " +
                    "(e.g. exclude: \"terminal.gui\").");
        }

        // ripgrep excludes via a negated glob; accept the bare name or an already-negated pattern.
        var exclude = input.GetStringPropertyOrEmpty("exclude");

        var args = new List<string>
        {
            "--line-number",
            "--color=never"
        };

        if (ignoreCase) args.Add("--ignore-case");
        if (contextLines > 0) args.Add($"--context={contextLines}");
        if (!string.IsNullOrEmpty(glob)) { args.Add("--glob"); args.Add(glob); }
        if (!string.IsNullOrEmpty(exclude)) { args.Add("--glob"); args.Add(exclude.StartsWith('!') ? exclude : "!" + exclude); }

        args.Add($"--max-count={MaxMatches}");
        args.Add("--");
        args.Add(pattern);
        args.Add(searchPath);

        var client = await GetClientAsync(ctx, ct);
        if (client is null)
            return ToolResult.Err(
                "ripgrep (rg) is not installed, so Grep can't run.\n" +
                "Install ripgrep and make sure 'rg' is on your PATH, set DOTSY_RIPGREP_PATH to an " +
                "existing rg binary, or re-run Grep and approve the one-time download when prompted.");

        var result = await client.RunRawAsync(args, ctx.Cwd, ct);

        if (string.IsNullOrEmpty(result.StandardOutput) && result.ExitCode == 1)
            return ToolResult.Ok("No matches found.");

        if (result.ExitCode != 0 && result.ExitCode != 1)
            return ToolResult.Err(string.IsNullOrWhiteSpace(result.StandardError)
                ? $"ripgrep exited with code {result.ExitCode}"
                : result.StandardError.Trim());

        return ToolResult.Ok(FormatOutput(result.StandardOutput));
    }

    // Resolves a ripgrep client, preferring an already-installed binary. If none is found, asks the
    // user for permission before letting Ivy.Ripgrep download one — never downloads silently.
    private async Task<RipgrepClient?> GetClientAsync(ToolContext ctx, CancellationToken ct)
    {
        if (_ripgrep is not null)
            return _ripgrep;

        var local = RipgrepBinary.FindLocal();
        if (local is not null)
            return _ripgrep = new RipgrepClient(new FixedRipgrepBinaryProvider(local), NullLogger<RipgrepClient>.Instance);

        // No local rg. Without an approval channel (e.g. headless), refuse rather than download.
        if (ctx.EmitEvent is null)
            return null;

        var tcs = new TaskCompletionSource<PermissionDecision>();
        await ctx.EmitEvent(new PermissionRequired(
            Name,
            "ripgrep (rg) is not installed. Download it (~5 MB) from github.com/BurntSushi/ripgrep so Grep can run?",
            tcs));

        var decision = await tcs.Task.WaitAsync(ct);
        if (decision == PermissionDecision.Deny)
            return null;

        // Approved: the default client provisions rg into the local cache (and reuses it afterwards).
        return _ripgrep = new RipgrepClient();
    }

    // Glob metacharacters that are invalid in a Windows path and signal a glob, not a directory.
    private static bool LooksLikeGlob(string value) =>
        !string.IsNullOrEmpty(value) && value.AsSpan().IndexOfAny('*', '?') >= 0;

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
