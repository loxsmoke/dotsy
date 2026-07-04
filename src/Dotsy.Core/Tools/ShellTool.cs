using System.Diagnostics;
using System.Text;
using System.Text.Json;

using Dotsy.Core.Utils;
using Dotsy.Core.Tools.Interfaces;

namespace Dotsy.Core.Tools;

public sealed class ShellTool : ITool
{
    public const string ToolName = "Shell";
    public string Name => ToolName;
    public string Description => "Execute a shell command. Captures combined stdout and stderr.";
    public const int MaxOutputChars = 30_000;
    public const int DefaultTimeoutMs = 30_000;
    public JsonElement InputSchema => ToolSchemas.ShellSchema;
    public ToolSafety Safety => ToolSafety.Destructive;
    public bool IsCompletionSignal => false;

    public string FormatRunApproval(JsonElement input, string cwd) =>
        input.GetStringPropertyOrEmpty("command");

    public string FormatPanelArgument(JsonElement input, string cwd) =>
        FormatRunApproval(input, cwd);

    public async Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var command = input.GetProperty("command").GetString() ?? "";
        int timeoutMs = input.TryGetProperty("timeout_ms", out var t) ? t.GetInt32() : DefaultTimeoutMs;

        if (WorktreeWipe(command) is { } blockedReason)
            return ToolResult.Err(blockedReason);

        var (fileName, args) = ParseCommand(command);

        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                WorkingDirectory = ctx.Cwd,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        var outputSb = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) { outputSb.AppendLine(e.Data); } };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) { outputSb.AppendLine(e.Data); } };

        try
        {
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            return ToolResult.Err($"Failed to start process: {ex.Message}");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);

        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            return ToolResult.Err($"Command timed out after {timeoutMs}ms");
        }

        var output = outputSb.ToString();
        if (output.Length > MaxOutputChars)
            output = ElideMiddle(output, MaxOutputChars);

        return proc.ExitCode == 0
            ? ToolResult.Ok(output)
            : ToolResult.Err($"Exit code {proc.ExitCode}\n{output}");
    }

    // Regexes for commands that discard ALL uncommitted work in one shot. Reverting a single named
    // file is left alone — only wholesale wipes are blocked.
    private static readonly (System.Text.RegularExpressions.Regex Rx, string What)[] WorktreeWipes =
    [
        (new(@"\bgit\s+reset\s+(--\S+\s+)*--hard\b", RxOpts), "git reset --hard"),
        (new(@"\bgit\s+checkout\s+(-f\s+|--force\s+|HEAD\s+|\S+\s+)*(--\s+)?(\.|:/|\*)(\s|$|&|;|\|)", RxOpts), "git checkout of the whole tree"),
        (new(@"\bgit\s+restore\s+(?!.*--staged\b)(--worktree\s+|--source\S*\s+|\S+\s+)*(--\s+)?(\.|:/|\*)(\s|$|&|;|\|)", RxOpts), "git restore of the whole tree"),
        (new(@"\bgit\s+clean\s+-\S*f", RxOpts), "git clean -f"),
    ];

    private const System.Text.RegularExpressions.RegexOptions RxOpts =
        System.Text.RegularExpressions.RegexOptions.IgnoreCase
        | System.Text.RegularExpressions.RegexOptions.CultureInvariant;

    /// <summary>
    /// Returns a block message if the command would discard all uncommitted changes in the working
    /// tree (reverting/resetting/cleaning everything) — a destructive action that can wipe in-progress
    /// work, including the agent's own edits. Returns null for safe commands (including reverting a
    /// single named file, which is allowed). This is a hard rail even under --yolo.
    /// </summary>
    internal static string? WorktreeWipe(string command)
    {
        foreach (var (rx, what) in WorktreeWipes)
        {
            if (rx.IsMatch(command))
                return $"Blocked: `{command.Trim()}` would discard ALL uncommitted changes in the working "
                     + $"tree ({what}), including work from this session. Do not revert the whole tree. Fix the "
                     + "specific problem forward with Edit/Write. To undo one file you broke, check out that "
                     + "single file by name (e.g. `git checkout -- path/to/File.cs`).";
        }
        return null;
    }

    private static (string fileName, string args) ParseCommand(string command)
    {
        if (OperatingSystem.IsWindows())
            return ("cmd.exe", $"/c {command}");
        return ("/bin/sh", $"-c \"{command.Replace("\"", "\\\"")}\"");
    }

    private static string ElideMiddle(string text, int maxChars)
    {
        int half = maxChars / 2 - 100;
        var head = text[..half];
        var tail = text[^half..];
        return head + $"\n\n<… {text.Length - maxChars} characters elided …>\n\n" + tail;
    }
}
