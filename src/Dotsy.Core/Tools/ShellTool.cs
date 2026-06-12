using System.Diagnostics;
using System.Text;
using System.Text.Json;

using Dotsy.Core.Utils;
using Dotsy.Core.Tools.Interfaces;

namespace Dotsy.Core.Tools;

public sealed class ShellTool : ITool
{
    private const int MaxOutputChars = 30_000;
    private const int DefaultTimeoutMs = 30_000;

    public string Name => "Shell";
    public string Description => "Execute a shell command. Captures combined stdout and stderr.";
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

        var (fileName, args) = ParseCommand(command);

        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                WorkingDirectory = ctx.Cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
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
