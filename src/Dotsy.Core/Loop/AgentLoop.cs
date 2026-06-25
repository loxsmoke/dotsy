using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Dotsy.Core.Config;
using Dotsy.Core.Git;
using Dotsy.Core.Loop.Data;
using Dotsy.Core.Providers;
using Dotsy.Core.Retrieval;
using Dotsy.Core.Session;
using Dotsy.Core.Session.Data;
using Dotsy.Core.Skills;
using Dotsy.Core.Tools;
using Dotsy.Core.Tools.Interfaces;

namespace Dotsy.Core.Loop;

public sealed class AgentLoop
{
    private readonly IProvider provider;
    private readonly ToolRegistry toolRegistry;
    private readonly PermissionStore permissions;
    private readonly DotsyConfig config;
    private readonly string? baseSystemPrompt;
    private readonly SessionStore? sessionStore;
    private readonly TrajectoryRecorder? trajectoryRecorder;

    public Func<ITool, string, CancellationToken, Task<PermissionDecision>>? PermissionPrompter { get; set; }

    public AgentLoop(
        IProvider provider,
        ToolRegistry registry,
        PermissionStore permissions,
        DotsyConfig config,
        string? baseSystemPrompt = null,
        SessionStore? sessionStore = null,
        TrajectoryRecorder? trajectory = null)
    {
        this.provider = provider;
        toolRegistry = registry;
        this.permissions = permissions;
        this.config = config;
        this.baseSystemPrompt = baseSystemPrompt;
        this.sessionStore = sessionStore;
        trajectoryRecorder = trajectory;
    }

    private static string ProviderDisplayName(string name) =>
        ConfigLoader.GetProviderDisplayName(name);

    public async IAsyncEnumerable<LoopEvent> RunAsync(
        LoopContext ctx,
        string cwd,
        [EnumeratorCancellation] CancellationToken ct)
    {
        int turn = 0;
        int nudgeCount = 0;
        var budget = ctx.TokenBudget;
        HashSet<string>? prevToolSigs = null;
        int consecutiveDuplicates = 0;
        int consecutiveErrorTurns = 0;
        // Per-turn tool-call signatures over a sliding window, for multi-turn cycle detection.
        var recentTurnSigs = new Queue<List<string>>();
        int toolEventIndex = 0;
        var retriedContextLengthError = false;

        // Records the reason the loop terminated into the session log, then returns the event to
        // yield. Routing every terminal LoopEnded through here keeps the JSONL diagnosable (e.g.
        // telling a clean Done from a nudge-limit bail-out, which both render as "idle" in the TUI).
        LoopEnded End(EndReason reason, string? message = null)
        {
            sessionStore?.Append(new SessionRecord
            {
                Type = SessionRecordType.End,
                Cwd = cwd,
                Message = message is null
                    ? new { reason = reason.ToString() }
                    : new { reason = reason.ToString(), message }
            });
            return new LoopEnded(reason, message);
        }

        while (!ct.IsCancellationRequested)
        {
            if (config.Agent.MaxTurns > 0 && turn >= config.Agent.MaxTurns)
            {
                yield return End(EndReason.TurnLimitReached);
                yield break;
            }

            // Compact before declaring the context full so a large previous turn can recover.
            if (config.Compaction.Enabled && ShouldCompact(budget))
            {
                var compacted = false;
                await foreach (var ev in CompactAsync(ctx, cwd, ct))
                {
                    compacted = true;
                    yield return ev;
                }

                if (compacted)
                    budget = ctx.TokenBudget;
            }

            // Check token budget
            if (budget.ContextWindow > 0 && budget.UsedTokens > budget.ContextWindow - budget.ReserveTokens)
            {
                yield return End(EndReason.ContextTooSmall);
                yield break;
            }

            // --- Build request ---
            var gitContext = GitContext.TryLoad(cwd);
            var skillDiscovery = new SkillDiscovery(config.Skills, cwd);
            var repoMap = BuildRepoMap(cwd, ctx);
            var systemPrompt = SystemPromptBuilder.Build(
                config,
                cwd,
                ctx,
                baseSystemPrompt,
                git: gitContext,
                skillDiscovery: skillDiscovery,
                repoMap: repoMap);
            var toolDefs = toolRegistry.GetToolDefinitions();
            ChatRequest? request = null;
            ContextTooSmallException? ctxError = null;
            try
            {
                request = RequestBuilder.Build(config, systemPrompt, ctx, toolDefs);
            }
            catch (ContextTooSmallException ex)
            {
                ctxError = ex;
            }

            if (ctxError is not null)
            {
                if (config.Compaction.Enabled)
                {
                    var compacted = false;
                    await foreach (var ev in CompactAsync(ctx, cwd, ct))
                    {
                        compacted = true;
                        yield return ev;
                    }

                    if (compacted)
                    {
                        budget = ctx.TokenBudget;
                        continue;
                    }
                }

                yield return End(EndReason.ContextTooSmall, ctxError.Message);
                yield break;
            }

            trajectoryRecorder?.CaptureInitialRequest(request!);

            // --- Stream response ---
            var textBuilder = new StringBuilder();
            var thinkingBuilder = new StringBuilder();
            var toolCalls = new List<(string Id, string Name, string Args)>();
            var pendingToolIndex = new Dictionary<string, int>(); // id -> LoopEvent index
            bool hadError = false;
            Exception? contextLengthError = null;
            int lastInputTokens = 0, lastOutputTokens = 0, lastCacheReadTokens = 0, lastCacheWriteTokens = 0;

            await foreach (var provEv in provider.StreamAsync(request!, ct))
            {
                switch (provEv)
                {
                    case TextDelta td:
                        textBuilder.Append(td.Text);
                        yield return new TextChunk(td.Text);
                        break;

                    case ThinkingDelta thk:
                        thinkingBuilder.Append(thk.Text);
                        yield return new ThinkingChunk(thk.Text);
                        break;

                    case ToolCallDelta tc:
                        toolCalls.Add((tc.Id, tc.Name, tc.ArgumentsJson));
                        yield return new ToolStarted(toolEventIndex, tc.Name, tc.ArgumentsJson);
                        pendingToolIndex[tc.Id] = toolEventIndex++;
                        break;

                    case UsageUpdate uu:
                        budget = budget.WithUsed(uu.InputTokens + uu.OutputTokens);
                        ctx.TokenBudget = budget;
                        trajectoryRecorder?.RecordUsage(
                            uu.InputTokens,
                            uu.OutputTokens,
                            uu.CacheReadTokens,
                            uu.CacheWriteTokens);
                        lastInputTokens = uu.InputTokens;
                        lastOutputTokens = uu.OutputTokens;
                        lastCacheReadTokens = uu.CacheReadTokens;
                        lastCacheWriteTokens = uu.CacheWriteTokens;
                        yield return new TokenUsageUpdated(
                            uu.InputTokens,
                            uu.OutputTokens,
                            uu.CacheReadTokens,
                            uu.CacheWriteTokens);
                        break;

                    case StreamEnd se:
                        break;

                     case StreamError serr:
                         if (IsContextLengthError(serr.Ex))
                         {
                             contextLengthError = serr.Ex;
                         }
                         else if (serr.Ex is ProviderException { Error: ModelUnknownError mue })
                         {
                             var models = await provider.GetModelsAsync(ct);
                             var modelList = string.Join("\n", models.Select(m => $"- {m.Id}"));
                             yield return End(EndReason.Error, 
                                 $"Model unknown: {mue.Message}\n\nAvailable models for {ProviderDisplayName(provider.Name)}:\n{modelList}");
                         }
                         else
                         {
                             yield return End(EndReason.Error,
                                 $"Error while invoking {ProviderDisplayName(provider.Name)} API\n{serr.Ex.Message}");
                         }
                         hadError = true;
                         break;
                }

                if (hadError) break;
            }

            if (contextLengthError is not null)
            {
                if (config.Compaction.Enabled && !retriedContextLengthError)
                {
                    var compacted = false;
                    await foreach (var ev in CompactAsync(ctx, cwd, ct))
                    {
                        compacted = true;
                        yield return ev;
                    }

                    if (compacted)
                    {
                        retriedContextLengthError = true;
                        budget = ctx.TokenBudget;
                        continue;
                    }
                }

                yield return End(EndReason.ContextTooSmall, contextLengthError.Message);
                yield break;
            }

            if (hadError)
                yield break;

            retriedContextLengthError = false;

            // Build assistant message
            var assistantBlocks = new List<ContentBlock>();
            if (textBuilder.Length > 0)
                assistantBlocks.Add(new TextBlock(textBuilder.ToString()));
            if (thinkingBuilder.Length > 0)
                assistantBlocks.Add(new ThinkingBlock(thinkingBuilder.ToString()));
            foreach (var (id, name, args) in toolCalls)
            {
                var argsJson = TryParseArgs(args);
                assistantBlocks.Add(new ToolUseBlock(id, name, argsJson));
            }
            ctx.Messages.Add(new AssistantMessage(assistantBlocks));

            // Log assistant turn
            if (sessionStore is not null && (textBuilder.Length > 0 || toolCalls.Count > 0))
            {
                object messageObj;
                if (toolCalls.Count > 0)
                {
                    var contentParts = new List<object>();
                    if (textBuilder.Length > 0)
                        contentParts.Add(new { type = "text", text = textBuilder.ToString() });
                    foreach (var (tcId, tcName, tcArgs) in toolCalls)
                        contentParts.Add(new { type = "tool_use", id = tcId, name = tcName, input = tcArgs });
                    messageObj = new { content = contentParts };
                }
                else
                {
                    messageObj = new { content = textBuilder.ToString() };
                }
                sessionStore.Append(new SessionRecord
                {
                    Type = SessionRecordType.Assistant,
                    Cwd = cwd,
                    Message = messageObj,
                    Usage = lastInputTokens > 0 || lastOutputTokens > 0
                        ? new SessionUsage
                        {
                            InputTokens = lastInputTokens,
                            OutputTokens = lastOutputTokens,
                            CacheReadTokens = lastCacheReadTokens,
                            CacheWriteTokens = lastCacheWriteTokens,
                            ContextWindowTokens = budget.ContextWindow,
                            MaxOutputTokens = request!.MaxTokens,
                            ReserveTokens = budget.ReserveTokens,
                            UsedTokens = budget.UsedTokens
                        }
                        : null
                });
            }

            // Nudge tracking — tool use resets nudge; turns with no tool calls count toward the limit
            if (toolCalls.Count > 0)
            {
                nudgeCount = 0;
            }
            else
            {
                nudgeCount++;
                if (config.Agent.NudgeLimit > 0 && nudgeCount >= config.Agent.NudgeLimit)
                {
                    yield return End(EndReason.NudgeLimitReached);
                    yield break;
                }
            }

            // --- Execute tool calls ---
            bool anyWriteTools = false;
            bool signalCompletion = false;
            ExecuteToolsResult? execResult = null;

            if (toolCalls.Count > 0)
            {
                // Detect exact duplicate tool calls from the previous turn (loop trap)
                var currentSigList = toolCalls.Select(tc => $"{tc.Name}:{tc.Args.Trim()}").ToList();
                var currentSigs = new HashSet<string>(currentSigList, StringComparer.Ordinal);
                bool isDuplicate = prevToolSigs is not null
                    && currentSigs.Count > 0
                    && currentSigs.SetEquals(prevToolSigs);

                // Rolling-window guard: catch multi-turn cycles (A,B,C,A,B,C…) the adjacent check
                // misses. Trips when every distinct call this turn has already recurred enough times
                // in the recent window that, counting this turn, it reaches RepeatThreshold.
                bool isRepeating = false;
                if (!isDuplicate && config.Agent.RepeatThreshold > 1 && currentSigs.Count > 0)
                {
                    var windowCounts = recentTurnSigs
                        .SelectMany(s => s)
                        .GroupBy(s => s, StringComparer.Ordinal)
                        .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
                    isRepeating = currentSigs.All(sig =>
                        windowCounts.TryGetValue(sig, out var c) && c + 1 >= config.Agent.RepeatThreshold);
                }

                if (isDuplicate || isRepeating)
                {
                    consecutiveDuplicates++;
                    string hint = isDuplicate
                        ? "You are repeating the exact same tool calls as the previous turn. "
                          + "Do not use any tools. Synthesize the information already returned and respond to the user in plain text."
                        : "You keep repeating the same reads and searches without making progress. "
                          + "Stop gathering information. Either make a concrete change with Edit or Write, "
                          + "or respond in plain text with what you found or why you are blocked.";
                    var blocks = new List<ContentBlock>();
                    for (int i = 0; i < toolCalls.Count; i++)
                    {
                        var (id, name, _) = toolCalls[i];
                        var idx = pendingToolIndex.TryGetValue(id, out var ti) ? ti : i;
                        yield return new ToolFinished(idx, name, ToolResult.Ok("[skipped: loop guard]"));
                        blocks.Add(new ToolResultBlock(id, hint, false));
                    }
                    ctx.Messages.Add(new UserMessage(blocks));

                    if (consecutiveDuplicates >= 2)
                    {
                        yield return new TurnComplete(budget.UsedTokens, false);
                        yield return End(EndReason.NudgeLimitReached);
                        yield break;
                    }
                }
                else
                {
                    consecutiveDuplicates = 0;
                    execResult = await ExecuteTools(ctx, cwd, toolCalls, ct);
                    anyWriteTools = execResult.AnyWriteTools;
                    signalCompletion = execResult.SignalCompletion;

                    var resultBlocks = new List<ContentBlock>();
                    for (int i = 0; i < toolCalls.Count; i++)
                    {
                        var (id, name, _) = toolCalls[i];
                        var result = execResult.Results[i].Result;
                        var idx = pendingToolIndex.TryGetValue(id, out var ti) ? ti : i;
                        yield return new ToolFinished(idx, name, result);
                        resultBlocks.Add(new ToolResultBlock(id, result.Content, result.IsError));
                    }
                    ctx.Messages.Add(new UserMessage(resultBlocks));

                    // Loop trap: the model keeps calling tools that all fail (often guessing file
                    // paths that don't exist). The exact-duplicate trap above misses this when the
                    // failing arguments vary turn to turn, so guard on consecutive all-error turns.
                    var allErrors = execResult.Results.Length > 0 && execResult.Results.All(r => r.Result.IsError);
                    if (allErrors)
                    {
                        consecutiveErrorTurns++;
                        if (consecutiveErrorTurns == 2)
                            ctx.Messages.Add(new UserMessage([new TextBlock(
                                "Every recent tool call failed. Stop guessing paths — use Grep or Glob to locate "
                                + "files before reading them, or tell the user plainly what you could not find. "
                                + "Do not repeat calls that already failed.")]));
                    }
                    else
                    {
                        consecutiveErrorTurns = 0;
                    }

                    if (sessionStore is not null)
                    {
                        var parts = new List<object>(toolCalls.Count);
                        for (int i = 0; i < toolCalls.Count; i++)
                        {
                            var (id, name, _) = toolCalls[i];
                            var result = execResult.Results[i];
                            parts.Add(new
                            {
                                type = "tool_result",
                                tool_use_id = id,
                                name,
                                content = result.Result.Content,
                                is_error = result.Result.IsError,
                                duration_ms = result.DurationMs,
                                approval_wait_ms = result.ApprovalWaitMs
                            });
                        }
                        sessionStore.Append(new SessionRecord
                        {
                            Type = SessionRecordType.ToolResult,
                            Cwd = cwd,
                            Message = new { content = parts }
                        });
                    }

                    prevToolSigs = currentSigs;

                    // Record this executed turn in the sliding window for cycle detection.
                    recentTurnSigs.Enqueue(currentSigList);
                    while (recentTurnSigs.Count > Math.Max(1, config.Agent.RepeatWindowTurns))
                        recentTurnSigs.Dequeue();

                    // Bail out of a failing-tool loop after a hint had a turn to land.
                    if (consecutiveErrorTurns >= 4)
                    {
                        yield return new TurnComplete(budget.UsedTokens, false);
                        yield return End(EndReason.NudgeLimitReached);
                        yield break;
                    }
                }
            }
            else
            {
                prevToolSigs = null;
            }

            if (anyWriteTools)
            {
                var git = new GitIntegration(cwd);
                if (config.Agent.AutoCommit)
                {
                    var firstLine = textBuilder.ToString()
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .FirstOrDefault() ?? "turn complete";
                    git.AutoCommit(ctx.SessionId, turn, firstLine, execResult?.AffectedPaths);
                }
                git.WriteCheckpoint(ctx.SessionId, turn);
            }

            yield return new TurnComplete(budget.UsedTokens, anyWriteTools);
            turn++;
            ctx.TurnCount = turn;

            if (config.Compaction.ToolPairSummarize)
                _ = ToolPairSummarizer.SummarizeOldPairsInBackground(ctx);

            if (signalCompletion)
            {
                yield return End(EndReason.TaskComplete);
                yield break;
            }

            // --- Reflection (section 16) ---
            if (anyWriteTools && (config.Agent.AutoLint || config.Agent.AutoTest))
            {
                var reflectResult = await RunReflection(ctx, cwd, ct);
                if (reflectResult is not null)
                {
                    if (ctx.Reflections >= config.Agent.MaxReflections)
                    {
                        yield return new ReflectionOccurred($"Max reflections ({config.Agent.MaxReflections}) reached");
                        ctx.Reflections = 0;
                    }
                    else
                    {
                        ctx.Reflections++;
                        yield return new ReflectionOccurred(reflectResult);
                        ctx.Messages.Add(new UserMessage([new TextBlock(reflectResult)]));
                        continue; // Re-enter loop with error context
                    }
                }
                else
                {
                    ctx.Reflections = 0;
                }
            }

            // --- Compaction check (section 18, done inline here) ---
            if (config.Compaction.Enabled && ShouldCompact(budget))
            {
                await foreach (var ev in CompactAsync(ctx, cwd, ct))
                    yield return ev;
                budget = ctx.TokenBudget;
            }

            // If no tool calls and model is done, check if we should end
            if (toolCalls.Count == 0)
            {
                // Model produced text without tools — let it continue unless nudge limit hit
            }
        }

        yield return End(EndReason.Cancelled);
    }

    private bool ShouldCompact(TokenBudget budget) =>
        budget.ContextWindow > 0
        && config.Compaction.ThresholdPct > 0
        && budget.UsagePct >= config.Compaction.ThresholdPct;

    private string? BuildRepoMap(string cwd, LoopContext ctx)
    {
        if (config.Retrieval.RepoMapTokens <= 0)
            return null;

        try
        {
            using var index = new RoslynIndex(Path.Combine(cwd, ".dotsy", "cache"));
            index.Open();
            var outlines = index.ScanDirectory(cwd);
            return RepoMap.Build(outlines, config.Retrieval.RepoMapTokens, GetRepoMapPersonalizationInputs(ctx));
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<string> GetRepoMapPersonalizationInputs(LoopContext ctx)
    {
        var files = new HashSet<string>(ctx.AddedFiles, StringComparer.OrdinalIgnoreCase);

        var latestUserText = ctx.Messages
            .OfType<UserMessage>()
            .LastOrDefault()?
            .Content
            .OfType<TextBlock>()
            .Select(b => b.Text)
            .LastOrDefault();

        if (!string.IsNullOrWhiteSpace(latestUserText))
        {
            foreach (var file in ExtractMentionedFiles(latestUserText))
                files.Add(file);
        }

        return files.ToList();
    }

    private static IEnumerable<string> ExtractMentionedFiles(string text)
    {
        const string FileChars = @"[A-Za-z0-9_\-./\\]";
        var pattern = $@"(?<!{FileChars}){FileChars}+\.(?:cs|csproj|sln|slnx|props|targets|json|toml|md|txt|xml|yaml|yml)(?!{FileChars})";

        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
            text,
            pattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(100)))
        {
            yield return match.Value.Trim('\'', '"', '`', ',', '.', ':', ';', ')', ']', '}');
        }
    }

    private static bool IsContextLengthError(Exception ex) =>
        ex is ProviderException { Error: ContextLengthError };

    private sealed class ExecuteToolsResult
    {
        public required ToolRunResult[] Results { get; init; }
        public bool AnyWriteTools { get; set; }
        public bool SignalCompletion { get; set; }
        public List<string> AffectedPaths { get; } = [];
    }

    private sealed record ToolRunResult(ToolResult Result, long DurationMs, long ApprovalWaitMs);

    private async Task<ExecuteToolsResult> ExecuteTools(
        LoopContext ctx,
        string cwd,
        List<(string Id, string Name, string Args)> toolCalls,
        CancellationToken ct)
    {
        var results = new ToolRunResult[toolCalls.Count];
        bool anyWriteTools = false;
        bool signalCompletion = false;

        // Partition by safety
        var readOnlyIndices = new List<int>();
        var serialIndices = new List<int>();

        for (int i = 0; i < toolCalls.Count; i++)
        {
            var (_, name, _) = toolCalls[i];
            if (toolRegistry.TryGetTool(name, out var tool) && tool is not null)
            {
                anyWriteTools |= tool.IsWriteTool;

                if (tool.Safety == ToolSafety.ReadOnly && config.Agent.ParallelTools)
                    readOnlyIndices.Add(i);
                else
                    serialIndices.Add(i);
            }
            else
            {
                serialIndices.Add(i);
            }
        }

        // Run ReadOnly tools in parallel
        if (readOnlyIndices.Count > 0)
        {
            var tasks = readOnlyIndices.Select(async i =>
            {
                var (_, toolName, args) = toolCalls[i];
                results[i] = await RunSingleTool(ctx, cwd, toolName, args, ct);
            });
            await Task.WhenAll(tasks);
        }

        // Run serial tools in order
        foreach (var i in serialIndices)
        {
            var (_, name, args) = toolCalls[i];
            results[i] = await RunSingleTool(ctx, cwd, name, args, ct);
        }

        // Check for completion signals across all executed tools
        var affectedPaths = new List<string>();
        foreach (var i in readOnlyIndices.Concat(serialIndices))
        {
            var (_, name, args) = toolCalls[i];
            if (toolRegistry.TryGetTool(name, out var completionTool) && completionTool?.IsCompletionSignal == true)
                signalCompletion = true;

            if (results[i] is { Result.IsError: false } && TryGetPathArgument(name, args) is { } path)
                affectedPaths.Add(path);
        }

        var execResult = new ExecuteToolsResult { Results = results, AnyWriteTools = anyWriteTools, SignalCompletion = signalCompletion };
        execResult.AffectedPaths.AddRange(affectedPaths.Distinct(StringComparer.OrdinalIgnoreCase));
        return execResult;
    }

    private static string? TryGetPathArgument(string toolName, string args)
    {
        if (!string.Equals(toolName, WriteTool.ToolName, StringComparison.Ordinal)
            && !string.Equals(toolName, EditTool.ToolName, StringComparison.Ordinal)
            && !string.Equals(toolName, MultiEditTool.ToolName, StringComparison.Ordinal))
            return null;

        var input = TryParseArgs(args);
        return input.TryGetProperty("path", out var pathEl) ? pathEl.GetString() : null;
    }

    private async Task<ToolRunResult> RunSingleTool(
        LoopContext ctx, string cwd, string toolName, string args, CancellationToken ct)
    {
        if (!toolRegistry.TryGetTool(toolName, out var tool) || tool is null)
            return new ToolRunResult(ToolResult.Err($"Unknown tool: {toolName}"), 0, 0);

        // Permission check
        var verdict = permissions.Evaluate(toolName, args);
        if (verdict == PermissionVerdict.Deny)
            return new ToolRunResult(ToolResult.Err($"Permission denied: {toolName}({args})"), 0, 0);

        long approvalWaitMs = 0;
        long nestedApprovalWaitMs = 0;

        if (verdict == PermissionVerdict.Ask && tool.Safety != ToolSafety.ReadOnly)
        {
            if (PermissionPrompter is not null)
            {
                var approvalSw = Stopwatch.StartNew();
                var decision = await PermissionPrompter(tool, args, ct);
                approvalSw.Stop();
                approvalWaitMs += approvalSw.ElapsedMilliseconds;
                switch (decision)
                {
                    case PermissionDecision.Deny:
                        return new ToolRunResult(ToolResult.Err($"Permission denied: {toolName}"), 0, approvalWaitMs);
                    case PermissionDecision.AlwaysAllow:
                        permissions.AlwaysAllow(toolName, args);
                        break;
                    case PermissionDecision.AllowForProject:
                        permissions.AllowWriteForProject();
                        break;
                    case PermissionDecision.AllowOnce:
                        permissions.AllowForSession(toolName, args);
                        break;
                }
            }
            else
            {
                return new ToolRunResult(ToolResult.Err($"Permission required for {toolName} - no interactive session"), 0, 0);
            }
        }

        var prompter = PermissionPrompter;
        var argsElement = TryParseArgs(args);
        var toolCtx = new ToolContext
        {
            Cwd = cwd,
            LoopContext = ctx,
            EmitEvent = prompter is null ? null : async ev =>
            {
                if (ev is PermissionRequired pr)
                {
                    var approvalSw = Stopwatch.StartNew();
                    var d = await prompter(tool, pr.DisplayArgument, ct);
                    approvalSw.Stop();
                    approvalWaitMs += approvalSw.ElapsedMilliseconds;
                    nestedApprovalWaitMs += approvalSw.ElapsedMilliseconds;
                    pr.Decision.TrySetResult(d);
                }
            }
        };

        var runSw = Stopwatch.StartNew();
        try
        {
            var result = await tool.ExecuteAsync(argsElement, toolCtx, ct);
            runSw.Stop();
            return new ToolRunResult(result, Math.Max(0, runSw.ElapsedMilliseconds - nestedApprovalWaitMs), approvalWaitMs);
        }
        catch (Exception ex)
        {
            runSw.Stop();
            return new ToolRunResult(ToolResult.Err($"{toolName} threw: {ex.Message}"), Math.Max(0, runSw.ElapsedMilliseconds - nestedApprovalWaitMs), approvalWaitMs);
        }
    }

    private async Task<string?> RunReflection(LoopContext ctx, string cwd, CancellationToken ct)
    {
        var sb = new StringBuilder();

        if (config.Agent.AutoLint)
        {
            var result = await RunShell("dotnet build --no-restore", cwd, ct);
            if (result.IsError)
                sb.AppendLine($"Build errors:\n{result.Content}");
        }

        if (config.Agent.AutoTest)
        {
            var result = await RunShell("dotnet test --no-build", cwd, ct);
            if (result.IsError)
                sb.AppendLine($"Test failures:\n{result.Content}");
        }

        return sb.Length > 0 ? sb.ToString().TrimEnd() : null;
    }

    private static async Task<ToolResult> RunShell(string command, string cwd, CancellationToken ct)
    {
        var tool = new ShellTool();
        var input = System.Text.Json.JsonDocument.Parse(
            $"{{\"command\":\"{command.Replace("\"", "\\\"")}\"}}").RootElement;
        var ctx = new ToolContext { Cwd = cwd, LoopContext = new LoopContext() };
        return await tool.ExecuteAsync(input, ctx, ct);
    }

    public async IAsyncEnumerable<LoopEvent> CompactAsync(
        LoopContext ctx,
        string cwd,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var tokensBefore = ctx.TokenBudget.UsedTokens;

        // Keep recent messages; summarize the rest
        var keepCount = EstimateTokensToKeep(ctx);
        var toSummarize = ctx.Messages.Take(ctx.Messages.Count - keepCount).ToList();

        if (toSummarize.Count == 0)
            yield break;

        var summaryPrompt = BuildSummaryPrompt(toSummarize);
        var summaryRequest = new ChatRequest(
            config.Model.ActiveModelId,
            "You are a concise summarizer. Summarize the conversation context below preserving key facts, decisions, and code changes.",
            [new UserMessage([new TextBlock(summaryPrompt)])],
            [],
            config.Compaction.ReserveTokens);

        var summary = new StringBuilder();
        await foreach (var ev in provider.StreamAsync(summaryRequest, ct))
        {
            if (ev is TextDelta td) summary.Append(td.Text);
            if (ev is StreamError) yield break;
        }

        // Incremental summary: merge with existing if present
        var newSummary = summary.ToString().Trim();
        if (newSummary.Length == 0)
            yield break;

        if (!string.IsNullOrEmpty(ctx.CompactionSummary))
            newSummary = ctx.CompactionSummary + "\n\n---\n\n" + newSummary;

        ctx.CompactionSummary = newSummary;

        sessionStore?.Append(new SessionRecord
        {
            Type = SessionRecordType.Summary,
            Cwd = cwd,
            Message = newSummary
        });
        ctx.Messages.RemoveRange(0, toSummarize.Count);
        ctx.TokenBudget = ctx.TokenBudget.WithUsed(EstimateCompactedTokens(ctx));

        yield return new CompactionOccurred(tokensBefore, ctx.TokenBudget.UsedTokens, ctx.CompactionSummary);
    }

    private int EstimateTokensToKeep(LoopContext ctx)
    {
        // Keep roughly the last KeepRecentTokens worth of messages (rough estimate: 4 chars/token)
        var keepChars = config.Compaction.KeepRecentTokens * 4;
        int chars = 0;
        int keep = 0;
        for (int i = ctx.Messages.Count - 1; i >= 0; i--)
        {
            var msg = ctx.Messages[i];
            int msgChars = MessageLength(msg);
            chars += msgChars;
            if (chars > keepChars) break;
            keep++;
        }
        return Math.Max(keep, 2);
    }

    private static int MessageLength(Message msg)
    {
        IReadOnlyList<ContentBlock> blocks = msg switch
        {
            UserMessage u => u.Content,
            AssistantMessage a => a.Content,
            _ => []
        };
        return blocks.Sum(b => b switch
        {
            TextBlock tb => tb.Text.Length,
            ToolResultBlock tr => tr.Content.Length,
            _ => 50
        });
    }

    private static int EstimateCompactedTokens(LoopContext ctx)
    {
        var chars = ctx.Messages.Sum(MessageLength);
        if (!string.IsNullOrEmpty(ctx.CompactionSummary))
            chars += ctx.CompactionSummary.Length;
        return Math.Max(1, chars / 4);
    }

    private static string BuildSummaryPrompt(List<Message> messages)
    {
        var sb = new StringBuilder("Summarize the following conversation:\n\n");
        foreach (var msg in messages)
        {
            sb.AppendLine($"[{msg.Role}]");
            IReadOnlyList<ContentBlock> blocks = msg switch
            {
                UserMessage u => u.Content,
                AssistantMessage a => a.Content,
                _ => []
            };
            foreach (var block in blocks)
            {
                if (block is TextBlock tb) sb.AppendLine(tb.Text);
                else if (block is ToolResultBlock tr) sb.AppendLine($"<tool_result>{tr.Content}</tool_result>");
            }
        }
        return sb.ToString();
    }

    private static System.Text.Json.JsonElement TryParseArgs(string args)
    {
        if (string.IsNullOrWhiteSpace(args))
            return System.Text.Json.JsonDocument.Parse("{}").RootElement;
        try
        {
            var el = System.Text.Json.JsonDocument.Parse(args).RootElement;
            // Some models double-encode tool arguments as a JSON string (e.g. "{\"path\":...}").
            // Unwrap one level when the string itself parses as an object/array.
            if (el.ValueKind == System.Text.Json.JsonValueKind.String
                && el.GetString() is { Length: > 0 } inner)
            {
                try
                {
                    var unwrapped = System.Text.Json.JsonDocument.Parse(inner).RootElement;
                    if (unwrapped.ValueKind is System.Text.Json.JsonValueKind.Object
                        or System.Text.Json.JsonValueKind.Array)
                        return unwrapped;
                }
                catch { /* not double-encoded JSON; fall through */ }
            }
            return el;
        }
        catch { return System.Text.Json.JsonDocument.Parse("{}").RootElement; }
    }
}
