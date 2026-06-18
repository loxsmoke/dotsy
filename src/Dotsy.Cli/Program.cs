using System.CommandLine;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Dotsy.Cli.Tui;
using Dotsy.Core.Config;
using Dotsy.Core.Loop;
using Dotsy.Core.Providers;
using Dotsy.Core.Session;
using Dotsy.Core.Tools;
using Dotsy.Mcp;
using Dotsy.Providers;
using Terminal.Gui;
using CliCommand = System.CommandLine.Command;

// Version information
const string Version = "1.0.0";
var currentDirectory = Environment.CurrentDirectory;
var config = ConfigLoader.Load(currentDirectory);

// ── root command ──────────────────────────────────────────────────────────
// Note: RootCommand's invocation pipeline registers a built-in `--version`
// option, so we don't add our own (doing so collides on the `--version` key).
var rootCommand = new RootCommand($"Dotsy — AI coding agent v{Version}");

var modelOption = new Option<string?>("--model", "Model ID override");
var providerOption = new Option<string?>("--provider", "Provider name override");
var maxTurnsOption = new Option<int?>("--max-turns", "Max turns override");

rootCommand.AddGlobalOption(modelOption);
rootCommand.AddGlobalOption(providerOption);
rootCommand.AddGlobalOption(maxTurnsOption);

// ── dotsy run ─────────────────────────────────────────────────────────────
var runCommand = new CliCommand("run", "Start an agent session");

var resumeOption = new Option<string?>("--resume", "Session ID to resume; omit value to resume most recent");
resumeOption.Arity = ArgumentArity.ZeroOrOne;
var bareOption = new Option<bool>("--bare", "Skip project config and hooks");
var noHistoryOption = new Option<bool>("--no-history", "Disable session history");
var yoloOption = new Option<bool>("--yolo", "Skip all permission prompts");
var promptOption = new Option<string?>(["-p", "--prompt"], "Single-shot prompt (headless)");
var fileOption = new Option<string?>(["-f", "--file"], "Read prompt from file (headless)");
var outputFormatOption = new Option<string>("--output-format", () => "text", "Output format: text|json|stream-json");

runCommand.AddOption(resumeOption);
runCommand.AddOption(bareOption);
runCommand.AddOption(noHistoryOption);
runCommand.AddOption(yoloOption);
runCommand.AddOption(promptOption);
runCommand.AddOption(fileOption);
runCommand.AddOption(outputFormatOption);

runCommand.SetHandler(async (ctx) =>
{
    var modelOverride = ctx.ParseResult.GetValueForOption(modelOption);
    var providerOverride = ctx.ParseResult.GetValueForOption(providerOption);
    var maxTurnsOverride = ctx.ParseResult.GetValueForOption(maxTurnsOption);
    var resumeId = ctx.ParseResult.GetValueForOption(resumeOption);
    // --resume with no value → "" sentinel means "load most recent"
    if (ctx.ParseResult.FindResultFor(resumeOption) is not null && resumeId is null)
        resumeId = "";
    var bare = ctx.ParseResult.GetValueForOption(bareOption);
    var noHistory = ctx.ParseResult.GetValueForOption(noHistoryOption);
    var yolo = ctx.ParseResult.GetValueForOption(yoloOption);
    var prompt = ctx.ParseResult.GetValueForOption(promptOption);
    var file = ctx.ParseResult.GetValueForOption(fileOption);
    var outputFormat = ctx.ParseResult.GetValueForOption(outputFormatOption);

    // Apply CLI overrides. Provider first so the model override lands in the right section.
    if (providerOverride is not null) config.Model.Provider = providerOverride;
    if (modelOverride is not null) config.Model.ActiveModelId = modelOverride;
    if (maxTurnsOverride.HasValue) config.Agent.MaxTurns = maxTurnsOverride.Value;

    // Read prompt from file if -f given
    if (file is not null && prompt is null)
        prompt = await File.ReadAllTextAsync(file, ctx.GetCancellationToken());

    // Auto-cleanup old sessions
    if (config.Session.CleanupDays > 0 && config.Session.LogEnabled)
    {
        var cleanDir = SessionStore.ResolveDir(config.Session.LogDir, currentDirectory);
        if (Directory.Exists(cleanDir))
            SessionStore.CleanOldSessions(cleanDir, config.Session.CleanupDays);
    }

    // TTY detection: headless if stdin redirected or -p/-f supplied
    bool isHeadless = Console.IsInputRedirected || prompt is not null;

    if (isHeadless)
    {
        var exitCode = await RunHeadless(
            config, currentDirectory, prompt ?? "", resumeId, noHistory, yolo, outputFormat ?? "text",
            ctx.GetCancellationToken());
        ctx.ExitCode = exitCode;
    }
    else
    {
        RunTui(config, currentDirectory, resumeId, noHistory, yolo, ctx.GetCancellationToken());
    }
});

rootCommand.AddCommand(runCommand);
// No subcommand → default to `run`.
rootCommand.SetHandler(() => runCommand.Invoke([]));
var skillsCommand = new CliCommand("skills", "Skills management");
var skillsListCommand = new CliCommand("list", "List discovered skills");
skillsListCommand.SetHandler(() =>
{
    var discovery = new SkillDiscovery(config.Skills, currentDirectory);
    var skills = discovery.FindAll();
    foreach (var s in skills)
        Console.WriteLine($"  {s.Name,-20}  {s.FilePath}");
    if (skills.Count == 0) Console.WriteLine("No skills found.");
});
skillsCommand.AddCommand(skillsListCommand);
rootCommand.AddCommand(skillsCommand);

// ── run ─────────────────────────────────────────────────────────────────────
return await rootCommand.InvokeAsync(args);

// ─────────────────────────────────────────────────────────────────────────────

static void RunTui(
    DotsyConfig config, string currentDirectory, string? resumeId, bool noHistory, bool yolo,
    CancellationToken ct)
{
    IProvider baseProvider;
    try
    {
        baseProvider = ProviderRegistry.Resolve(config);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Config error: {ex.Message}");
        return;
    }

    var provider = new RetryingProvider(baseProvider, onRetry: (rs, _) =>
    {
        TuiSessionContext.StatusUpdate?.Invoke(
            $"⏳ retrying in {rs.DelaySeconds}s · attempt {rs.AttemptNumber}/{rs.MaxAttempts}");
        return Task.CompletedTask;
    });

    var skillDiscovery = new SkillDiscovery(config.Skills, currentDirectory);
    var permissions    = new PermissionStore(config.Permissions, currentDirectory) { Yolo = yolo };
    var registry       = ToolRegistry.CreateWithBuiltIns(skillDiscovery);
    WireSubTasks(config, currentDirectory, registry, permissions);
    TuiSessionContext.StartupMessages.Clear();
    var mcpManager = ConnectMcpServers(config, registry, msg =>
        TuiSessionContext.StartupMessages.Add(msg));

    // Session setup
    var sessionDir = SessionStore.ResolveDir(config.Session.LogDir, currentDirectory);
    string sessionId;
    LoopContext loopCtx;

    if (resumeId is not null)
    {
        var loaded = SessionLoader.Load(resumeId, sessionDir) ?? SessionLoader.LoadMostRecent(sessionDir, currentDirectory);
        if (loaded is not null)
        {
            sessionId = loaded.SessionId;
            loopCtx = new LoopContext(sessionId);
            loopCtx.Messages.AddRange(loaded.Messages);
            loopCtx.CompactionSummary = loaded.CompactionSummary;
        }
        else
        {
            sessionId = SessionStore.NextId(sessionDir);
            loopCtx = new LoopContext(sessionId);
        }
    }
    else
    {
        sessionId = SessionStore.NextId(sessionDir);
        loopCtx = new LoopContext(sessionId);
    }

    var sessionStore = new SessionStore(sessionId, sessionDir, noHistory || !config.Session.LogEnabled);
    var trajectory = new TrajectoryRecorder(config, currentDirectory);

    // End agent turn after one text-only response so the user can reply
    config.Agent.NudgeLimit = 1;

    var loop = new AgentLoop(provider, registry, permissions, config, sessionStore: sessionStore, trajectory: trajectory);

    TuiSessionContext.Config      = config;
    TuiSessionContext.Cwd         = currentDirectory;
    TuiSessionContext.ProjectConfigPath = ConfigLoader.FindProjectConfig(currentDirectory);
    TuiSessionContext.Loop        = loop;
    TuiSessionContext.LoopCtx     = loopCtx;
    TuiSessionContext.Permissions = permissions;
    TuiSessionContext.Registry    = registry;
    TuiSessionContext.Session     = sessionStore;
    TuiSessionContext.Trajectory  = trajectory;
    TuiSessionContext.McpManager  = mcpManager;

    // Delegate that rebuilds the provider+loop from the live config object.
    // Called by AgentWindow when model.* config keys are changed at runtime.
    TuiSessionContext.LoopFactory = () =>
    {
        var newBase = ProviderRegistry.Resolve(TuiSessionContext.Config);
        var newProvider = new RetryingProvider(newBase, onRetry: (rs, _) =>
        {
            TuiSessionContext.StatusUpdate?.Invoke(
                $"⏳ retrying in {rs.DelaySeconds}s · attempt {rs.AttemptNumber}/{rs.MaxAttempts}");
            return Task.CompletedTask;
        });
        return new AgentLoop(newProvider, TuiSessionContext.Registry!, permissions, TuiSessionContext.Config, sessionStore: TuiSessionContext.Session, trajectory: TuiSessionContext.Trajectory);
    };

    // Live model-info lookup for /model. Resolves a provider from the current config each call so
    // it reflects model/provider switches made via /config or /model at runtime.
    TuiSessionContext.ModelInfoLookup = modelId =>
        ProviderRegistry.Resolve(TuiSessionContext.Config).GetModelInfoAsync(modelId, CancellationToken.None);

    // Ctrl+Break on Windows fires ConsoleSpecialKey.ControlBreak via CancelKeyPress;
    // Terminal.Gui never sees it as a KeyDown, so we intercept it here and stop cleanly.
    Console.CancelKeyPress += (_, e) =>
    {
        if (e.SpecialKey == ConsoleSpecialKey.ControlBreak)
        {
            e.Cancel = true;
            Application.Invoke(() => Application.RequestStop());
        }
    };

    // Resolve and apply the configured color theme before any view is built. Unknown names
    // fall back to dark with a warning; "system" resolves to dark/light by terminal detection.
    var (resolvedTheme, themeFellBack) = Palette.Apply(config.Tui.Theme);
    if (themeFellBack)
        Console.Error.WriteLine(
            $"warning: unknown tui.theme '{config.Tui.Theme}'; using 'dark'. " +
            $"Valid: {string.Join(", ", Themes.Names)}");

    Application.Init();
    try
    {
        var scheme = Palette.Scheme();
        Colors.ColorSchemes["Base"]   = scheme;
        Colors.ColorSchemes["Menu"]   = scheme;
        Colors.ColorSchemes["Dialog"] = scheme;
        Colors.ColorSchemes["Error"]  = scheme;
        Application.Run<AgentWindow>();
    }
    finally
    {
        Application.Shutdown();
        mcpManager.Dispose();
        TuiSessionContext.McpManager = null;
        TuiSessionContext.Trajectory = null;
    }
}

static async Task<int> RunHeadless(
    DotsyConfig config, string workingDirectory, string prompt, string? resumeId,
    bool noHistory, bool yolo, string outputFormat, CancellationToken ct)
{
    if (string.IsNullOrWhiteSpace(prompt))
    {
        Console.Error.WriteLine("No prompt provided. Use -p or -f.");
        return 1;
    }

    var sw = Stopwatch.StartNew();
    IProvider provider;
    try
    {
        provider = ProviderRegistry.Resolve(config);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Config error: {ex.Message}");
        return 2;
    }

    var skillDiscovery = new SkillDiscovery(config.Skills, workingDirectory);
    var permissions = new PermissionStore(config.Permissions, workingDirectory) { Yolo = yolo };
    var registry = ToolRegistry.CreateWithBuiltIns(skillDiscovery);
    WireSubTasks(config, workingDirectory, registry, permissions);
    using var mcpManager = ConnectMcpServers(config, registry, msg =>
    {
        if (outputFormat == "stream-json")
            Console.WriteLine(JsonSerializer.Serialize(new { type = "McpLog", data = new { message = msg } }));
        else
            Console.Error.WriteLine(msg);
    });

    // Session setup
    var sessionDir = SessionStore.ResolveDir(config.Session.LogDir, workingDirectory);
    string sessionId;
    LoopContext loopCtx;

    if (resumeId is not null)
    {
        var loaded = SessionLoader.Load(resumeId, sessionDir) ?? SessionLoader.LoadMostRecent(sessionDir, workingDirectory);
        if (loaded is not null)
        {
            sessionId = loaded.SessionId;
            loopCtx = new LoopContext(sessionId);
            loopCtx.Messages.AddRange(loaded.Messages);
            loopCtx.CompactionSummary = loaded.CompactionSummary;
        }
        else
        {
            sessionId = SessionStore.NextId(sessionDir);
            loopCtx = new LoopContext(sessionId);
        }
    }
    else
    {
        sessionId = SessionStore.NextId(sessionDir);
        loopCtx = new LoopContext(sessionId);
    }

    var sessionStore = new SessionStore(sessionId, sessionDir, noHistory || !config.Session.LogEnabled);
    var trajectory = new TrajectoryRecorder(config, workingDirectory);

    // Get model info for token budget
    var modelInfo = await provider.GetModelInfoAsync(config.Model.ActiveModelId, ct);
    loopCtx.TokenBudget = new TokenBudget(
        modelInfo.ContextWindow,
        config.Compaction.ReserveTokens,
        config.Compaction.KeepRecentTokens,
        0);

    var loop = new AgentLoop(provider, registry, permissions, config, sessionStore: sessionStore, trajectory: trajectory);
    if (prompt.Trim().Equals("/compact", StringComparison.OrdinalIgnoreCase))
    {
        var compactExitCode = await RunHeadlessCompact(loop, loopCtx, workingDirectory, outputFormat, sw, ct);
        sessionStore.UpdateIndex("manual compaction", workingDirectory, config.Model.ActiveModelId);
        return compactExitCode;
    }

    loopCtx.Messages.Add(new UserMessage([new TextBlock(prompt)]));
    sessionStore.Append(new SessionRecord
    {
        Type = "user",
        Cwd = workingDirectory,
        Message = new { content = prompt }
    });

    var tokenUsageTracker = new TokenUsageTracker();
    var resultSb = new StringBuilder();
    var exitCode = 0;
    LoopEnded? finalEnd = null;

    if (outputFormat == "stream-json")
    {
        await foreach (var ev in loop.RunAsync(loopCtx, workingDirectory, ct))
        {
            if (ev is LoopEnded le)
            {
                finalEnd = le;
                exitCode = le.Reason switch
                {
                    EndReason.Error => 1,
                    EndReason.ContextTooSmall => 4,
                    EndReason.Cancelled => 130,
                    _ => 0
                };
            }
            var line = JsonSerializer.Serialize(new { type = ev.GetType().Name, data = ev });
            Console.WriteLine(line);
        }
    }
    else
    {
        await foreach (var ev in loop.RunAsync(loopCtx, workingDirectory, ct))
        {
            switch (ev)
            {
                case TextChunk tc:
                    if (outputFormat == "text")
                        Console.Write(tc.Text);
                    else
                        resultSb.Append(tc.Text);
                    break;
                case TokenUsageUpdated tu:
                    tokenUsageTracker.RecordUsage(new UsageUpdate(
                        tu.InputTokens,
                        tu.OutputTokens,
                        tu.CacheReadTokens,
                        tu.CacheWriteTokens));
                    break;
                case LoopEnded le:
                    finalEnd = le;
                    exitCode = le.Reason switch
                    {
                        EndReason.Error => 1,
                        EndReason.ContextTooSmall => 4,
                        EndReason.Cancelled => 130,
                        _ => 0
                    };
                    break;
            }
        }

        if (outputFormat == "json")
        {
            sw.Stop();
            var output = new
            {
                result = resultSb.ToString(),
                sessionId = loopCtx.SessionId,
                inputTokens = tokenUsageTracker.TotalInputTokens,
                outputTokens = tokenUsageTracker.TotalOutputTokens,
                durationMs = sw.ElapsedMilliseconds
            };
            Console.WriteLine(JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
        }
        else if (outputFormat == "text")
        {
            Console.WriteLine(); // final newline
        }
    }

    var indexTitle = prompt.Length > 50 ? prompt[..50] + "…" : prompt;
    sessionStore.UpdateIndex(indexTitle, workingDirectory, config.Model.ActiveModelId);
    TryExportTrajectory(trajectory, loopCtx, finalEnd ?? new LoopEnded(EndReason.Cancelled), msg =>
    {
        if (outputFormat == "stream-json")
            Console.WriteLine(JsonSerializer.Serialize(new { type = "Warning", data = new { message = msg } }));
        else
            Console.Error.WriteLine(msg);
    });

    return exitCode;
}

static void TryExportTrajectory(
    TrajectoryRecorder trajectory,
    LoopContext loopCtx,
    LoopEnded ended,
    Action<string> warn)
{
    try
    {
        trajectory.Export(loopCtx, ended.Reason, ended.Message);
    }
    catch (Exception ex)
    {
        warn($"[warn] trajectory export failed: {ex.Message}");
    }
}

static async Task<int> RunHeadlessCompact(
    AgentLoop loop,
    LoopContext loopCtx,
    string cwd,
    string outputFormat,
    Stopwatch sw,
    CancellationToken ct)
{
    CompactionOccurred? compacted = null;

    if (outputFormat == "stream-json")
    {
        await foreach (var ev in loop.CompactAsync(loopCtx, cwd, ct))
        {
            if (ev is CompactionOccurred co)
                compacted = co;
            Console.WriteLine(JsonSerializer.Serialize(new { type = ev.GetType().Name, data = ev }));
        }
    }
    else
    {
        await foreach (var ev in loop.CompactAsync(loopCtx, cwd, ct))
        {
            if (ev is CompactionOccurred co)
                compacted = co;
        }
    }

    if (outputFormat == "json")
    {
        sw.Stop();
        var output = new
        {
            result = compacted is null ? "nothing to compact" : "compacted",
            sessionId = loopCtx.SessionId,
            tokensBefore = compacted?.TokensBefore,
            tokensAfter = compacted?.TokensAfter,
            durationMs = sw.ElapsedMilliseconds
        };
        Console.WriteLine(JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
    }
    else if (outputFormat == "text")
    {
        if (compacted is null)
            Console.WriteLine("nothing to compact");
        else
            Console.WriteLine($"compacted ({compacted.TokensBefore:N0}->{compacted.TokensAfter:N0} tokens)");
    }

    return 0;
}

static void WireSubTasks(
    DotsyConfig config,
    string cwd,
    ToolRegistry registry,
    PermissionStore permissions)
{
    if (!registry.TryGetTool(TaskTool.ToolName, out var tool) || tool is not TaskTool taskTool)
        return;

    var manager = new AgentSubTaskManager(
        () => ProviderRegistry.Resolve(config),
        registry,
        permissions,
        config,
        cwd);
    taskTool.LaunchSubTask = manager.LaunchAsync;
    taskTool.GetSubTaskStatus = manager.GetStatusAsync;
}

static McpManager ConnectMcpServers(
    DotsyConfig config,
    ToolRegistry registry,
    Action<string>? log)
{
    var manager = new McpManager();
    if (config.Mcp.Servers.Count == 0)
        return manager;

    try
    {
        manager.ConnectAllAsync(config.Mcp.Servers, registry, log).GetAwaiter().GetResult();
    }
    catch (Exception ex)
    {
        log?.Invoke($"[warn] MCP startup failed: {ex.Message}");
    }

    return manager;
}
