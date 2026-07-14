using System.CommandLine;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Dotsy.Cli;
using Dotsy.Cli.SlashCommands;
using Dotsy.Cli.Tui;
using Dotsy.Cli.Tui.Colors;
using Dotsy.Core.Config;
using Dotsy.Core.Loop;
using Dotsy.Core.Loop.Data;
using Dotsy.Core.Providers;
using Dotsy.Core.Session;
using Dotsy.Core.Session.Data;
using Dotsy.Core.Skills;
using Dotsy.Core.Tools;
using Dotsy.Core.Utils;
using Dotsy.Mcp;
using Dotsy.Providers;

// Version information
const string Version = "1.0.0";
var currentDirectory = Environment.CurrentDirectory;
var config = ConfigLoader.Load(currentDirectory);

// Note: RootCommand's invocation pipeline registers a built-in `--version`
// option, so we don't add our own (doing so collides on the `--version` key).
var rootCommand = new RootCommand($"Dotsy - AI coding agent v{Version}");

var modelOption = new Option<string?>("--model") { Description = "Model ID override" };
var providerOption = new Option<string?>("--provider") { Description = "Provider name override" };
var maxTurnsOption = new Option<int?>("--max-turns") { Description = "Max turns override" };

rootCommand.Options.Add(modelOption);
rootCommand.Options.Add(providerOption);
rootCommand.Options.Add(maxTurnsOption);

// ---- dotsy run ----------------------------------------------------------------------------------------------------------------
var runCommand = new System.CommandLine.Command("run", "Start an agent session");

var resumeOption = new Option<string?>("--resume") { Description = "Session ID to resume; omit value to resume most recent" };
resumeOption.Arity = ArgumentArity.ZeroOrOne;
var bareOption = new Option<bool>("--bare") { Description = "Skip project config and hooks" };
var noHistoryOption = new Option<bool>("--no-history") { Description = "Disable session history" };
var yoloOption = new Option<bool>("--yolo") { Description = "Skip all permission prompts" };
var promptOption = new Option<string?>("--prompt") { Description = "Single-shot prompt (headless)" };
promptOption.Aliases.Add("-p");
var fileOption = new Option<string?>("--file") { Description = "Read prompt from file (headless)" };
fileOption.Aliases.Add("-f");
var outputFormatOption = new Option<string>("--output-format")
{
    Description = "Output format: text|json|stream-json",
    DefaultValueFactory = _ => "text"
};

runCommand.Options.Add(resumeOption);
runCommand.Options.Add(bareOption);
runCommand.Options.Add(noHistoryOption);
runCommand.Options.Add(yoloOption);
runCommand.Options.Add(promptOption);
runCommand.Options.Add(fileOption);
runCommand.Options.Add(outputFormatOption);

runCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var modelOverride = parseResult.GetValue(modelOption);
    var providerOverride = parseResult.GetValue(providerOption);
    var maxTurnsOverride = parseResult.GetValue(maxTurnsOption);
    var resumeId = parseResult.GetValue(resumeOption);
    // --resume with no value â†’ "" sentinel means "load most recent"
    if (parseResult.GetResult(resumeOption) is not null && resumeId is null)
        resumeId = "";
    var bare = parseResult.GetValue(bareOption);
    var noHistory = parseResult.GetValue(noHistoryOption);
    var yolo = parseResult.GetValue(yoloOption);
    var prompt = parseResult.GetValue(promptOption);
    var file = parseResult.GetValue(fileOption);
    var outputFormat = parseResult.GetValue(outputFormatOption);

    // Apply CLI overrides. Provider first so the model override lands in the right section.
    if (providerOverride is not null) config.Model.Provider = providerOverride;
    if (modelOverride is not null) config.Model.ActiveModel.Id = modelOverride;
    if (maxTurnsOverride.HasValue) config.Agent.MaxTurns = maxTurnsOverride.Value;

    // Read prompt from file if -f given
    if (file is not null && prompt is null)
        prompt = await File.ReadAllTextAsync(file, cancellationToken);

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
            cancellationToken);
        return exitCode;
    }
    else
    {
        await RunTui(config, currentDirectory, resumeId, noHistory, yolo, cancellationToken);
        return 0;
    }
});

rootCommand.Subcommands.Add(runCommand);
// No subcommand. default to `run`.
rootCommand.SetAction(_ => runCommand.Parse([]).Invoke()); 

var skillsCommand = new System.CommandLine.Command("skills", "Skills management");
var skillsListCommand = new System.CommandLine.Command("list", "List discovered skills");
skillsListCommand.SetAction(async (_, ct) =>
{
    var discovery = new SkillDiscovery(config.Skills, currentDirectory);
    var skills = discovery.FindAll();
    foreach (var s in skills)
        Console.WriteLine($"  {s.Name,-20}  {s.FilePath}");
    if (skills.Count == 0) Console.WriteLine("No skills found.");
    return 0;
});
skillsCommand.Subcommands.Add(skillsListCommand);
rootCommand.Subcommands.Add(skillsCommand);

// ---- run ----------------------------------------------------------------------------------------------------------------
return await rootCommand.Parse(args).InvokeAsync();

// --------------------------------------------------------------------------------------------------------------------------
static async Task RunTui(
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
            $"retrying in {rs.DelaySeconds}s - attempt {rs.AttemptNumber}/{rs.MaxAttempts}");
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

    // Tokens already in context for a resumed session; 0 for a fresh one. Seeds the budget below so
    // launching straight into a resumed session shows the real usage rather than 0%.
    int restoredUsedTokens = 0;
    LoadedSession? loadedSession = null;

    if (resumeId is not null)
    {
        var loaded = SessionLoader.Load(resumeId, sessionDir) ?? SessionLoader.LoadMostRecent(sessionDir, currentDirectory);
        if (loaded is not null)
        {
            loadedSession = loaded;
            sessionId = loaded.SessionId;
            loopCtx = new LoopContext(sessionId);
            loopCtx.Messages.AddRange(loaded.Messages);
            loopCtx.CompactionSummary = loaded.CompactionSummary;
            restoredUsedTokens = loaded.UsedTokens;
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

    // Size the token budget to the model's active context window so compaction triggers at the
    // right point (for Ollama this honors model.ollama.max_context_tokens, sent as num_ctx).
    var modelInfo = await provider.GetModelInfoAsync(config.Model.ActiveModel.Id, ct);
    loopCtx.TokenBudget = new TokenBudget(
        modelInfo.ContextWindow,
        config.Compaction.ReserveTokens,
        config.Compaction.KeepRecentTokens,
        restoredUsedTokens,
        config.Compaction.ThresholdPct);

    // Interactive sessions yield to the user after a configurable number of stalled responses
    // (default 3). Auto-continue (agent.auto_continue_on_nudge) can still recover a stall before
    // the turn ends. Set agent.interactive_nudge_limit = 1 to restore the old yield-immediately UX.
    config.Agent.NudgeLimit = config.Agent.InteractiveNudgeLimit;

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
    TuiSessionContext.StartupLoadedSession = loadedSession;

    // Delegate that rebuilds the provider+loop from the live config object.
    // Called by AgentWindow when model.* config keys are changed at runtime.
    TuiSessionContext.LoopFactory = () =>
    {
        var newBase = ProviderRegistry.Resolve(TuiSessionContext.Config);
        var newProvider = new RetryingProvider(newBase, onRetry: (rs, _) =>
        {
            TuiSessionContext.StatusUpdate?.Invoke(
                $"retrying in {rs.DelaySeconds}s - attempt {rs.AttemptNumber}/{rs.MaxAttempts}");
            return Task.CompletedTask;
        });
        return new AgentLoop(newProvider, TuiSessionContext.Registry!, permissions, TuiSessionContext.Config, sessionStore: TuiSessionContext.Session, trajectory: TuiSessionContext.Trajectory);
    };

    // Live model-info lookup for /model. Resolves a provider from the current config each call so
    // it reflects model/provider switches made via /config or /model at runtime.
    TuiSessionContext.ModelInfoLookup = modelId =>
        ProviderRegistry.Resolve(TuiSessionContext.Config).GetModelInfoAsync(modelId, CancellationToken.None);
    TuiSessionContext.ModelListLookup = ct =>
        ProviderRegistry.Resolve(TuiSessionContext.Config).GetModelsAsync(ct);

    // Ctrl+Break on Windows fires ConsoleSpecialKey.ControlBreak via CancelKeyPress;
    // Terminal.Gui never sees it as a KeyDown, so we intercept it here and stop cleanly.
    Console.CancelKeyPress += (_, e) =>
    {
        if (e.SpecialKey == ConsoleSpecialKey.ControlBreak)
        {
            e.Cancel = true;
            TuiSessionContext.App.Invoke(() => TuiSessionContext.App.RequestStop());
        }
    };

    // Resolve and apply the configured color theme before any view is built. Unknown names
    // fall back to dark with a warning; "system" resolves to dark/light by terminal detection.
    var (resolvedTheme, themeFellBack) = Palette.Apply(config.Tui.Theme);
    if (themeFellBack)
        Console.Error.WriteLine(
            $"warning: unknown tui.theme '{config.Tui.Theme}'; using '{resolvedTheme}'. " +
            $"Valid: {string.Join(", ", Themes.Names)}");

    if (Application.MaximumIterationsPerSecond < 60)
        Application.MaximumIterationsPerSecond = 60;

    var app = Application.Create();
    TuiSessionContext.App = app;
    app.Init();
    EnsureFullScreenOnStartup(app);
    try
    {
        app.Run<AgentWindow>();
    }
    finally
    {
        app.Dispose();
        mcpManager.Dispose();
        TuiSessionContext.McpManager = null;
        TuiSessionContext.Trajectory = null;
        TuiSessionContext.StartupLoadedSession = null;
    }
}

// Make the TUI fill the whole console at startup.
//
// The Terminal.Gui v2 Windows driver renders into a freshly created console screen
// buffer and reads its size from that buffer (WindowsOutput.GetWindowSize). A new
// screen buffer reports a default window size rather than the real console window, so
// the UI sometimes starts confined to a corner of the terminal. The driver's size
// monitor only raises a resize when the reported size *changes*, so that first wrong
// value sticks until the user manually resizes the window - which is exactly the
// workaround users hit today.
//
// We mirror that manual resize. The Iteration event fires at the start of each main
// loop iteration, *before* the driver polls its size, so a single assertion is
// overwritten by the first poll. Re-asserting the real console dimensions for the
// first few iterations lets the correct size take and then hold (once the monitor has
// cached the stale value it stays quiet, so our value is no longer clobbered).
static void EnsureFullScreenOnStartup(IApplication app)
{
    const int requiredStableIterations = 3;
    const int maxCorrectionIterations = 10;
    var stable = 0;
    var attempts = 0;

    // Apply once now, then keep it pinned for the first handful of iterations.
    ApplyConsoleSize(app);

    EventHandler<EventArgs<IApplication?>>? onIteration = null;
    onIteration = (_, _) =>
    {
        if (++attempts >= maxCorrectionIterations)
        {
            app.Iteration -= onIteration;
            return;
        }

        if (ApplyConsoleSize(app))
        {
            if (++stable >= requiredStableIterations)
                app.Iteration -= onIteration;
        }
        else
        {
            stable = 0;
        }
    };
    app.Iteration += onIteration;
}

// Forces the driver and application screen to the real console window size. Returns
// true when they already matched (nothing changed), false when a correction was applied.
static bool ApplyConsoleSize(IApplication app)
{
    int width, height;
    try
    {
        width = Console.WindowWidth;
        height = Console.WindowHeight;
    }
    catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
    {
        // Redirected or non-console hosts do not expose synchronous window dimensions.
        // Leave Terminal.Gui's own size detection in charge and stop re-asserting.
        return true;
    }

    if (width <= 0 || height <= 0)
        return true;

    var changed = false;

    if (app.Driver is { } driver && (driver.Cols != width || driver.Rows != height))
    {
        driver.Cols = width;
        driver.Rows = height;
        changed = true;
    }

    if (app.Screen.Width != width || app.Screen.Height != height)
    {
        app.Screen = new System.Drawing.Rectangle(0, 0, width, height);
        changed = true;
    }

    if (changed)
        app.LayoutAndDraw(true);

    return !changed;
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

    // No interactive user to answer clarifying questions in a headless run.
    config.Agent.Headless = true;

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

    // Tokens already in context for a resumed session; 0 for a fresh one. Seeds the budget below so
    // a resumed headless run continues from the real usage rather than restarting the gauge at 0.
    int restoredUsedTokens = 0;

    if (resumeId is not null)
    {
        var loaded = SessionLoader.Load(resumeId, sessionDir) ?? SessionLoader.LoadMostRecent(sessionDir, workingDirectory);
        if (loaded is not null)
        {
            sessionId = loaded.SessionId;
            loopCtx = new LoopContext(sessionId);
            loopCtx.Messages.AddRange(loaded.Messages);
            loopCtx.CompactionSummary = loaded.CompactionSummary;
            restoredUsedTokens = loaded.UsedTokens;
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
    var modelInfo = await provider.GetModelInfoAsync(config.Model.ActiveModel.Id, ct);
    loopCtx.TokenBudget = new TokenBudget(
        modelInfo.ContextWindow,
        config.Compaction.ReserveTokens,
        config.Compaction.KeepRecentTokens,
        restoredUsedTokens,
        config.Compaction.ThresholdPct);

    var loop = new AgentLoop(provider, registry, permissions, config, sessionStore: sessionStore, trajectory: trajectory);
    if (prompt.Trim().EqualsNoCase($"/{CompactCommand.CommandName}"))
    {
        var compactExitCode = await RunHeadlessCompact(loop, loopCtx, workingDirectory, outputFormat, sw, ct);
        sessionStore.UpdateIndex("manual compaction", workingDirectory, config.Model.ActiveModel.Id);
        return compactExitCode;
    }

    loopCtx.Messages.Add(new UserMessage([new TextBlock(prompt)]));
    sessionStore.Append(new SessionRecord
    {
        Type = SessionRecordType.User,
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
            var line = HeadlessStreamJson.Format(ev);
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
                        tu.CacheWriteTokens,
                        tu.ServerDurationMs), tu.DurationMs);
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
                durationMs = sw.ElapsedMilliseconds,
                tokensPerSecond = tokenUsageTracker.OutputTokensPerSecond is { } tps
                    ? Math.Round(tps, 1)
                    : (double?)null
            };
            Console.WriteLine(JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
        }
        else if (outputFormat == "text")
        {
            Console.WriteLine(); // final newline
        }
    }

    var indexTitle = prompt.Length > 50 ? prompt[..50] + "..." : prompt;
    sessionStore.UpdateIndex(indexTitle, workingDirectory, config.Model.ActiveModel.Id);
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
            Console.WriteLine(HeadlessStreamJson.Format(ev));
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
