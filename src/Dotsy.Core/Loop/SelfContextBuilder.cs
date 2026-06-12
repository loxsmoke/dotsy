using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Dotsy.Core.Config;
using Dotsy.Core.Providers;
using Dotsy.Core.Tools;

namespace Dotsy.Core.Loop;

public sealed record SelfContextRequest(
    DotsyConfig Config,
    LoopContext LoopContext,
    string Cwd,
    ToolRegistry? Registry = null,
    IReadOnlyList<SlashCommand>? Commands = null,
    string Mode = "tui",
    DateTimeOffset? GeneratedAt = null,
    string? ExecutablePath = null,
    int MaxChars = 30_000,
    TimeSpan? ProbeTimeout = null);

public sealed class SelfContextBuilder
{
    private const string Unknown = "unknown";

    public async Task<string> BuildPromptAsync(SelfContextRequest request, string? question, CancellationToken ct = default)
    {
        var context = await BuildMarkdownAsync(request, ct);
        var userQuestion = string.IsNullOrWhiteSpace(question)
            ? "Summarize the current Dotsy runtime and notable configuration concisely."
            : question.Trim();

        return $"{context}\n\n## User Question\n\n{userQuestion}";
    }

    public async Task<string> BuildMarkdownAsync(SelfContextRequest request, CancellationToken ct = default)
    {
        var timeout = request.ProbeTimeout ?? TimeSpan.FromMilliseconds(500);
        var generatedAt = request.GeneratedAt ?? DateTimeOffset.Now;

        var gitTask = ProbeAsync(() => FolderSnapshot.Load(request.Cwd), timeout, ct);
        var systemTask = ProbeAsync(SystemSnapshot.Load, timeout, ct);
        var gpuTask = ProbeAsync(GpuSnapshot.Load, timeout, ct);

        await Task.WhenAll(gitTask, systemTask, gpuTask);

        var sb = new StringBuilder();
        sb.AppendLine("# Dotsy Self Context");
        sb.AppendLine();
        sb.AppendLine($"Generated: {generatedAt:O}");
        sb.AppendLine();
        AppendApp(sb, request);
        AppendFolder(sb, gitTask.Result, request.Cwd);
        AppendConfiguration(sb, request.Config);
        AppendSystem(sb, systemTask.Result);
        AppendGpu(sb, gpuTask.Result);
        AppendCommands(sb, request.Commands ?? SlashCommandCatalog.Commands);
        AppendTools(sb, request.Registry);

        return Cap(sb.ToString().TrimEnd(), request.MaxChars);
    }

    private static void AppendApp(StringBuilder sb, SelfContextRequest request)
    {
        var assembly = typeof(SelfContextBuilder).Assembly;
        var infoVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? Unknown;

        AppendSection(sb, "App");
        AppendTable(sb, ["Field", "Value"],
        [
            ["Name", "dotsy"],
            ["Executable path", request.ExecutablePath ?? Environment.ProcessPath ?? Unknown],
            ["Version", infoVersion],
            ["Process ID", Environment.ProcessId.ToString()],
            ["Session ID", request.LoopContext.SessionId],
            ["Provider", request.Config.Model.Provider],
            ["Model", request.Config.Model.Id],
            ["Mode", request.Mode]
        ]);
    }

    private static void AppendFolder(StringBuilder sb, FolderSnapshot? folder, string cwd)
    {
        AppendSection(sb, "Folder");
        AppendTable(sb, ["Field", "Value"],
        [
            ["CWD", cwd],
            ["Git root", folder?.GitRoot ?? Unknown],
            ["Git branch", folder?.Branch ?? Unknown],
            ["Git short SHA", folder?.ShortSha ?? Unknown],
            ["Modified files", folder?.ModifiedCount.ToString() ?? Unknown],
            ["Untracked files", folder?.UntrackedCount.ToString() ?? Unknown],
            ["Detected solution/project files", folder?.ProjectFiles ?? Unknown],
            ["Project .dotsy/config.toml", folder?.ProjectConfig ?? Unknown],
            ["Agent instruction files", folder?.InstructionFiles ?? Unknown]
        ]);
    }

    private static void AppendConfiguration(StringBuilder sb, DotsyConfig config)
    {
        AppendSection(sb, "Configuration");
        AppendTable(sb, ["Key", "Type", "Value", "Source", "Description"],
            ConfigEditor.ParamList.SelectMany(g => g.Params.Select(p =>
            {
                var raw = GetConfigValue(config, p.Key);
                return new[]
                {
                    p.Key,
                    p.Type,
                    IsSecretKey(p.Key) ? RedactSecret(raw) : FormatValue(raw),
                    InferSource(config, p.Key, raw),
                    p.Description
                };
            })).ToList());
    }

    private static void AppendSystem(StringBuilder sb, SystemSnapshot? system)
    {
        AppendSection(sb, "System");
        AppendTable(sb, ["Field", "Value"],
        [
            ["Platform", system?.Platform ?? Unknown],
            ["Architecture", system?.Architecture ?? Unknown],
            [".NET", system?.DotNet ?? Unknown],
            ["Shell", system?.Shell ?? Unknown],
            ["Host name", system?.HostName ?? Unknown],
            ["Logical CPUs", system?.LogicalCpus ?? Unknown],
            ["Process start time", system?.ProcessStartTime ?? Unknown],
            ["Process uptime", system?.Uptime ?? Unknown],
            ["Process memory MB", system?.ProcessMemoryMb ?? Unknown],
            ["Available system memory MB", system?.AvailableMemoryMb ?? Unknown],
            ["Process threads", system?.ProcessThreads ?? Unknown],
            ["Process CPU percent", system?.ProcessCpuPercent ?? Unknown]
        ]);
    }

    private static void AppendGpu(StringBuilder sb, GpuSnapshot? gpu)
    {
        AppendSection(sb, "GPU");
        AppendTable(sb, ["Field", "Value"],
        [
            ["Present", gpu?.Present ?? Unknown],
            ["Model", gpu?.Model ?? Unknown],
            ["Driver/runtime", gpu?.Driver ?? Unknown],
            ["Total memory MB", gpu?.TotalMemoryMb ?? Unknown],
            ["Available memory MB", gpu?.AvailableMemoryMb ?? Unknown],
            ["Process memory MB", gpu?.ProcessMemoryMb ?? Unknown]
        ]);
    }

    private static void AppendCommands(StringBuilder sb, IReadOnlyList<SlashCommand> commands)
    {
        AppendSection(sb, "Commands");
        AppendTable(sb, ["Syntax", "Description"],
            commands.Select(c => new[] { c.Syntax, c.Description }).ToList());
    }

    private static void AppendTools(StringBuilder sb, ToolRegistry? registry)
    {
        AppendSection(sb, "Tools");
        var tools = registry?.GetToolDefinitions().OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList() ?? [];
        sb.AppendLine($"Registered tools: {tools.Count}");
        sb.AppendLine();
        if (tools.Count == 0)
        {
            sb.AppendLine("No registered tool definitions are available in this context.");
            sb.AppendLine();
            return;
        }

        AppendTable(sb, ["Name", "Description"],
            tools.Select(t => new[] { t.Name, t.Description }).ToList());
    }

    private static async Task<T?> ProbeAsync<T>(Func<T> probe, TimeSpan timeout, CancellationToken ct) where T : class
    {
        try
        {
            return await Task.Run(probe, ct).WaitAsync(timeout, ct);
        }
        catch
        {
            return null;
        }
    }

    private static void AppendSection(StringBuilder sb, string heading)
    {
        sb.AppendLine($"## {heading}");
        sb.AppendLine();
    }

    private static void AppendTable(StringBuilder sb, string[] headers, IReadOnlyList<string[]> rows)
    {
        sb.Append("| ").AppendJoin(" | ", headers.Select(EscapeTableCell)).AppendLine(" |");
        sb.Append("| ").AppendJoin(" | ", headers.Select(_ => "---")).AppendLine(" |");
        foreach (var row in rows)
            sb.Append("| ").AppendJoin(" | ", row.Select(EscapeTableCell)).AppendLine(" |");
        sb.AppendLine();
    }

    private static string EscapeTableCell(string? value) =>
        (string.IsNullOrWhiteSpace(value) ? "(empty)" : value)
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Replace("|", "\\|")
            .Trim();

    private static string Cap(string value, int maxChars)
    {
        if (maxChars <= 0 || value.Length <= maxChars)
            return value;

        var suffix = "\n\n[Self-context truncated to fit prompt-size cap.]";
        var keep = Math.Max(0, maxChars - suffix.Length);
        return value[..keep].TrimEnd() + suffix;
    }

    private static bool IsSecretKey(string key) =>
        key.EndsWith(".api_key", StringComparison.OrdinalIgnoreCase)
        || key.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || key.Contains("auth_token", StringComparison.OrdinalIgnoreCase)
        || key.Contains("access_token", StringComparison.OrdinalIgnoreCase)
        || key.Contains("bearer", StringComparison.OrdinalIgnoreCase)
        || key.Contains("password", StringComparison.OrdinalIgnoreCase);

    private static string RedactSecret(object? value)
    {
        var raw = value?.ToString();
        if (string.IsNullOrWhiteSpace(raw))
            return "not set";

        var trimmed = raw.Trim();
        var lower = trimmed.ToLowerInvariant();
        if (lower is "todo" or "changeme" or "change-me" or "your-api-key" or "<api-key>" or "api-key"
            || lower.Contains("example")
            || lower.StartsWith("sk-"))
            return "placeholder";

        return "set:redacted";
    }

    private static string FormatValue(object? value)
    {
        if (value is null)
            return "not set";
        if (value is string s)
            return string.IsNullOrWhiteSpace(s) ? "not set" : s;
        if (value is bool b)
            return b.ToString().ToLowerInvariant();
        return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "not set";
    }

    private static string InferSource(DotsyConfig config, string key, object? value)
    {
        if (IsSecretKey(key) && GetKnownSecretEnvVar(key) is { } envName)
        {
            var envValue = Environment.GetEnvironmentVariable(envName);
            if (!string.IsNullOrEmpty(envValue) && string.Equals(envValue, value?.ToString(), StringComparison.Ordinal))
                return $"env:{envName}";
        }

        var defaultValue = GetConfigValue(DefaultConfig.Create(), key);
        return Equals(ValueKey(defaultValue), ValueKey(value)) ? "default" : "current";
    }

    private static string? GetKnownSecretEnvVar(string key) => key.ToLowerInvariant() switch
    {
        "model.anthropic.api_key" => "ANTHROPIC_API_KEY",
        "model.openai.api_key" => "OPENAI_API_KEY",
        "model.azure.api_key" => "AZURE_OPENAI_API_KEY",
        "model.compatible.api_key" => "OPENAI_API_KEY",
        _ => null
    };

    private static string? ValueKey(object? value) =>
        value switch
        {
            null => null,
            bool b => b.ToString().ToLowerInvariant(),
            _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
        };

    private static object? GetConfigValue(DotsyConfig config, string key)
    {
        object? current = config;
        foreach (var part in key.Split('.'))
        {
            if (current is null)
                return null;
            var property = current.GetType().GetProperty(ToPropertyName(part), BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            current = property?.GetValue(current);
        }
        return current;
    }

    private static string ToPropertyName(string key) =>
        key == "left-panel-width-percentage"
            ? nameof(TuiConfig.LeftPanelWidthPercentage)
            : string.Concat(key.Split('_').Select(w => w.Length > 0 ? char.ToUpperInvariant(w[0]) + w[1..] : w));

    private sealed record FolderSnapshot(
        string GitRoot,
        string Branch,
        string ShortSha,
        int ModifiedCount,
        int UntrackedCount,
        string ProjectFiles,
        string ProjectConfig,
        string InstructionFiles)
    {
        public static FolderSnapshot Load(string cwd)
        {
            var git = GitContext.TryLoad(cwd);
            var gitRoot = Unknown;
            try
            {
                var repoPath = LibGit2Sharp.Repository.Discover(cwd);
                if (!string.IsNullOrWhiteSpace(repoPath))
                    gitRoot = Path.GetFullPath(Path.Combine(repoPath, ".."));
            }
            catch { }

            return new FolderSnapshot(
                gitRoot,
                git?.Branch ?? Unknown,
                git?.ShortSha ?? Unknown,
                git?.ModifiedCount ?? 0,
                git?.UntrackedCount ?? 0,
                SummarizeFiles(cwd, ["*.sln", "*.slnx", "*.csproj"], 12),
                File.Exists(Path.Combine(cwd, ".dotsy", "config.toml")) ? "present" : "not present",
                SummarizeFiles(cwd, ["AGENTS.md", "agents.md", ".agents", ".codex"], 12));
        }
    }

    private sealed record SystemSnapshot(
        string Platform,
        string Architecture,
        string DotNet,
        string Shell,
        string HostName,
        string LogicalCpus,
        string ProcessStartTime,
        string Uptime,
        string ProcessMemoryMb,
        string AvailableMemoryMb,
        string ProcessThreads,
        string ProcessCpuPercent)
    {
        public static SystemSnapshot Load()
        {
            using var process = Process.GetCurrentProcess();
            var start = process.StartTime;
            var cpuStart = process.TotalProcessorTime;
            var wallStart = Stopwatch.GetTimestamp();
            Thread.Sleep(50);
            process.Refresh();
            var wallElapsed = Stopwatch.GetElapsedTime(wallStart).TotalMilliseconds;
            var cpuElapsed = (process.TotalProcessorTime - cpuStart).TotalMilliseconds;
            var cpuPct = wallElapsed > 0
                ? cpuElapsed / (wallElapsed * Math.Max(1, Environment.ProcessorCount)) * 100.0
                : 0;

            return new SystemSnapshot(
                RuntimeInformation.OSDescription,
                RuntimeInformation.OSArchitecture.ToString(),
                Environment.Version.ToString(),
                DetectShell(),
                Environment.MachineName,
                Environment.ProcessorCount.ToString(),
                start.ToString("O"),
                (DateTime.Now - start).ToString(@"dd\.hh\:mm\:ss"),
                (process.WorkingSet64 / 1024 / 1024).ToString(),
                GetAvailableMemoryMb(),
                process.Threads.Count.ToString(),
                cpuPct.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private sealed record GpuSnapshot(
        string Present,
        string Model,
        string Driver,
        string TotalMemoryMb,
        string AvailableMemoryMb,
        string ProcessMemoryMb)
    {
        public static GpuSnapshot Load()
        {
            var nvidia = TryRun("nvidia-smi", "--query-gpu=name,driver_version,memory.total,memory.free --format=csv,noheader,nounits", TimeSpan.FromMilliseconds(250));
            if (!string.IsNullOrWhiteSpace(nvidia))
            {
                var first = nvidia.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                var parts = first.Split(',').Select(p => p.Trim()).ToArray();
                if (parts.Length >= 4)
                    return new GpuSnapshot("true", parts[0], parts[1], parts[2], parts[3], Unknown);
            }

            return new GpuSnapshot(Unknown, Unknown, Unknown, Unknown, Unknown, Unknown);
        }
    }

    private static string DetectShell()
    {
        if (OperatingSystem.IsWindows())
            return Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
        return Environment.GetEnvironmentVariable("SHELL") ?? "/bin/sh";
    }

    private static string GetAvailableMemoryMb()
    {
        try
        {
            var info = GC.GetGCMemoryInfo();
            if (info.TotalAvailableMemoryBytes > 0)
                return (info.TotalAvailableMemoryBytes / 1024 / 1024).ToString();
        }
        catch { }
        return Unknown;
    }

    private static string SummarizeFiles(string cwd, string[] patterns, int max)
    {
        try
        {
            var files = patterns
                .SelectMany(pattern => Directory.EnumerateFiles(cwd, pattern, SearchOption.TopDirectoryOnly))
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Take(max + 1)
                .ToList();

            if (files.Count == 0)
                return "none";

            var suffix = files.Count > max ? ", ..." : "";
            return string.Join(", ", files.Take(max)) + suffix;
        }
        catch
        {
            return Unknown;
        }
    }

    private static string? TryRun(string fileName, string arguments, TimeSpan timeout)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            if (!proc.Start())
                return null;
            if (!proc.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            return proc.StandardOutput.ReadToEnd();
        }
        catch
        {
            return null;
        }
    }
}
