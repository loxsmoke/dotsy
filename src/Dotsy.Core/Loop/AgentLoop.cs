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
using Dotsy.Core.Utils;

namespace Dotsy.Core.Loop;

public sealed partial class AgentLoop
{
    /// <summary>
    /// The provider used to generate responses and stream events. This is typically an LLM provider.
    /// </summary>
    private readonly IProvider provider;
    /// <summary>
    /// The registry of available tools that the agent can invoke. Tools are registered with their names and metadata.
    /// </summary>
    private readonly ToolRegistry toolRegistry;
    /// <summary>
    /// The permission store that tracks which tools and actions are allowed or denied for the agent. 
    /// It is used to enforce safety and security policies.
    /// </summary>
    private readonly PermissionStore permissions;
    /// <summary>
    /// The configuration settings for the agent loop, including parameters for compaction, 
    /// skill discovery, and other behaviors.
    /// </summary>
    private readonly DotsyConfig config;
    /// <summary>
    /// Optional base system prompt that can be used to initialize the agent's context. This prompt can provide
    /// initial instructions or context for the agent's behavior.
    /// </summary>
    private readonly string? baseSystemPrompt;
    /// <summary>
    /// Optional session store that records the agent's interactions, tool calls, and other events 
    /// for auditing or debugging purposes.
    /// </summary>
    private readonly SessionStore? sessionStore;
    /// <summary>
    /// Optional trajectory recorder that captures the sequence of events and decisions 
    /// made by the agent during its loop.
    /// </summary>
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

    public async IAsyncEnumerable<LoopEvent> RunAsync(
        LoopContext ctx,
        string workingDirectory,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var cwd = workingDirectory;
        int turn = 0;
        int nudgeCount = 0;
        int autoContinueAttempts = 0;
        var budget = ctx.TokenBudget;
        HashSet<string>? prevToolSigs = null;
        int consecutiveDuplicates = 0;
        int consecutiveErrorTurns = 0;
        // Per-turn tool-call signatures over a sliding window, for multi-turn cycle detection.
        var recentTurnSigs = new Queue<List<string>>();
        int toolEventIndex = 0;
        var retriedContextLengthError = false;

        while (!ct.IsCancellationRequested)
        {
            // Check turn limit
            if (config.Agent.MaxTurns > 0 && turn >= config.Agent.MaxTurns)
            {
                yield return End(cwd, EndReason.TurnLimitReached);
                yield break;
            }

            // Compact before declaring the context full so a large previous turn can recover.
            if (config.Compaction.Enabled && budget.ShouldCompact)
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
            if (budget.ContextWindow > 0 && budget.UsedTokens > budget.Usable)
            {
                yield return End(cwd, EndReason.ContextTooSmall);
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

                yield return End(cwd, EndReason.ContextTooSmall, ctxError.Message);
                yield break;
            }

            trajectoryRecorder?.CaptureInitialRequest(request!);

            var response = new TurnResponse(toolEventIndex, budget);
            await foreach (var ev in StreamResponseAsync(request!, ctx, cwd, response, ct))
                yield return ev;
            toolEventIndex = response.NextToolEventIndex;
            budget = response.Budget;

            if (response.ContextLengthError is not null)
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

                yield return End(cwd, EndReason.ContextTooSmall, response.ContextLengthError.Message);
                yield break;
            }

            if (response.HadError)
                yield break;

            retriedContextLengthError = false;

            AppendAssistantTurn(ctx, cwd, response, request!);
            var textBuilder = response.Text;
            var toolCalls = response.ToolCalls;
            var stopReason = response.StopReason;

            // Nudge tracking — tool use resets nudge. A normal text-only EndTurn is a completed
            // response, not a request to invoke the model again. Tool use is also the progress
            // signal for auto-continue: it resets the retry budget so only a genuinely stalled
            // agent (no progress between nudges) exhausts the attempts.
            if (toolCalls.Count > 0)
            {
                nudgeCount = 0;
                autoContinueAttempts = 0;
            }
            else if (stopReason is not (StopReason.EndTurn or StopReason.StopSequence))
            {
                nudgeCount++;
                if (config.Agent.NudgeLimit > 0 && nudgeCount >= config.Agent.NudgeLimit)
                {
                    // Recoverable stall: instead of ending, inject a targeted hint and give the
                    // model another nudge window — bounded by auto_continue_max_attempts.
                    if (config.Agent.AutoContinueOnNudge
                        && autoContinueAttempts < config.Agent.AutoContinueMaxAttempts)
                    {
                        autoContinueAttempts++;
                        nudgeCount = 0;
                        var hint = stopReason == StopReason.MaxTokens
                            ? "Your previous response was cut off by the output length limit. "
                              + "Continue from exactly where you left off; do not restart."
                            : "Your previous response did not make progress on the task. "
                              + "Take the next concrete action now (use a tool to make a change or gather what you still need), "
                              + "or, if the task is already complete, give your final answer to the user in plain text.";
                        ctx.Messages.Add(new UserMessage([new TextBlock(hint)]));
                        yield return new AutoContinued(
                            autoContinueAttempts,
                            config.Agent.AutoContinueMaxAttempts,
                            stopReason == StopReason.MaxTokens ? "truncated output" : "no progress");
                        continue;
                    }

                    var reason = stopReason == StopReason.MaxTokens
                        ? EndReason.MaxTokens
                        : EndReason.NoProgress;
                    yield return End(cwd, reason);
                    yield break;
                }
            }

            // --- Execute tool calls ---
            // Tool-loop state remains local to this run; helper methods handle the individual paths.
            bool anyWriteTools = false;
            bool signalCompletion = false;
            ExecuteToolsResult? execResult = null;

            if (toolCalls.Count > 0)
            {
                // Detect exact duplicate tool calls from the previous turn (loop trap)
                var repetition = DetectToolCallRepetition(toolCalls, prevToolSigs, recentTurnSigs);

                // Rolling-window guard: catch multi-turn cycles (A,B,C,A,B,C…) the adjacent check
                // misses. Trips when every distinct call this turn has already recurred enough times
                // in the recent window that, counting this turn, it reaches RepeatThreshold.
                if (repetition.IsDuplicate || repetition.IsRepeating)
                {
                    consecutiveDuplicates++;
                    foreach (var ev in ApplyToolLoopGuard(ctx, response, repetition.IsDuplicate, consecutiveDuplicates))
                        yield return ev;

                    // Weak models can get stuck re-reading the same files without ever editing.
                    // Rather than bail after the first repeat, give a few escalating, progress-guarded
                    // chances to break out and act (the guard skips the repeated calls and the hint gets
                    // firmer each time). Bounded by the auto-continue budget so a hopeless loop still ends.
                    var repeatBailAt = config.Agent.AutoContinueOnNudge
                        ? 1 + Math.Max(1, config.Agent.AutoContinueMaxAttempts)
                        : 2;
                    if (consecutiveDuplicates >= repeatBailAt)
                    {
                        yield return new TurnComplete(budget.UsedTokens, false);
                        yield return End(cwd, EndReason.Repetition);
                        yield break;
                    }

                    yield return new AutoContinued(
                        consecutiveDuplicates,
                        repeatBailAt - 1,
                        "repeated tool calls — redirecting to make an edit");
                }
                else
                {
                    consecutiveDuplicates = 0;
                    (execResult, consecutiveErrorTurns, var Events) =
                        await ExecuteToolTurnAsync(ctx, cwd, response, consecutiveErrorTurns, ct);
                    anyWriteTools = execResult.AnyWriteTools;
                    signalCompletion = execResult.SignalCompletion;

                    foreach (var ev in Events)
                        yield return ev;

                    prevToolSigs = repetition.Signatures;

                    // Record this executed turn in the sliding window for cycle detection.
                    recentTurnSigs.Enqueue(repetition.SignatureList);
                    while (recentTurnSigs.Count > Math.Max(1, config.Agent.RepeatWindowTurns))
                        recentTurnSigs.Dequeue();

                    // Bail out of a failing-tool loop after a hint had a turn to land.
                    if (consecutiveErrorTurns >= 4)
                    {
                        yield return new TurnComplete(budget.UsedTokens, false);
                        yield return End(cwd, EndReason.ToolErrorStreak);
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
                GitCheckpoint(cwd, ctx, textBuilder, turn, execResult?.AffectedPaths);
            }

            yield return new TurnComplete(budget.UsedTokens, anyWriteTools);
            turn++;
            ctx.TurnCount = turn;

            if (config.Compaction.ToolPairSummarize)
                _ = ToolPairSummarizer.SummarizeOldPairsInBackground(
                    ctx, preserveLatestReads: config.Compaction.PreserveLatestReads);

            if (signalCompletion)
            {
                // Completion guard: weaker models often signal Done while narrating a "build
                // succeeded" that never happened — the most recent build actually exited non-zero.
                // Refuse the completion, tell the model the build is red, and retry. Bounded by
                // AutoContinueMaxAttempts and progress-guarded (a passing build clears the flag),
                // so a genuine green completion still ends immediately.
                if (config.Agent.VerifyBuildBeforeComplete
                    && ctx.LastBuildFailed == true
                    && ctx.BuildGuardTrips < config.Agent.AutoContinueMaxAttempts)
                {
                    ctx.BuildGuardTrips++;
                    nudgeCount = 0;
                    autoContinueAttempts = 0;
                    ctx.Messages.Add(new UserMessage([new TextBlock(
                        "You signaled completion, but the most recent build/test command exited "
                        + "non-zero — the build is FAILING. The task is NOT complete; do not claim "
                        + "success. Re-run the build, read the exact errors, fix them by editing "
                        + "files, and only signal completion after the build exits 0.")]));
                    yield return new AutoContinued(
                        ctx.BuildGuardTrips,
                        config.Agent.AutoContinueMaxAttempts,
                        "completion signaled over a failing build");
                    continue;
                }

                yield return End(cwd, EndReason.TaskComplete);
                yield break;
            }

            if (toolCalls.Count == 0
                && stopReason is StopReason.EndTurn or StopReason.StopSequence)
            {
                // A clean text-only turn normally means the model is done. But weaker models often
                // announce the next step ("Let me implement this now.") and end the turn without
                // acting — no work happens. Treat that as a recoverable stall: nudge and retry
                // instead of stopping. Bounded by the shared auto-continue budget and progress-
                // guarded (a tool call next turn resets it), so a genuine final answer still ends.
                var endText = textBuilder.ToString();
                var announced = AgentLoopHeuristics.LooksLikeAnnouncedNextStep(endText);
                // In a headless run there is no user to answer, so a turn that ends by asking the
                // user to clarify is a dead end — nudge the model to proceed on its own instead.
                var askedUser = config.Agent.Headless && AgentLoopHeuristics.LooksLikeQuestionToUser(endText);
                if (config.Agent.AutoContinueOnEndTurnIntent
                    && autoContinueAttempts < config.Agent.AutoContinueMaxAttempts
                    && (announced || askedUser))
                {
                    autoContinueAttempts++;
                    nudgeCount = 0;
                    var hint = askedUser
                        ? "You are running autonomously with no user available to answer questions. "
                          + "Do not ask for clarification. Make reasonable assumptions and complete the "
                          + "task now by editing files with tools (e.g. Edit or Write)."
                        : "You described the next step but did not take it. Do not just explain — "
                          + "take that action now by calling a tool (e.g. Edit or Write to make the "
                          + "change). If the task is already complete, say so explicitly as your "
                          + "final answer instead.";
                    ctx.Messages.Add(new UserMessage([new TextBlock(hint)]));
                    yield return new AutoContinued(
                        autoContinueAttempts,
                        config.Agent.AutoContinueMaxAttempts,
                        askedUser ? "asked the user a question in headless mode" : "announced next step without acting");
                    continue;
                }

                yield return End(cwd, EndReason.ResponseComplete);
                yield break;
            }

            var reflection = await ProcessReflectionAsync(ctx, cwd, anyWriteTools, ct);
            if (reflection.Event is not null)
                yield return reflection.Event;
            if (reflection.ShouldContinue)
                continue;

            // --- Compaction check (section 18, done inline here) ---
            if (config.Compaction.Enabled && budget.ShouldCompact)
            {
                await foreach (var ev in CompactAsync(ctx, cwd, ct))
                    yield return ev;
                budget = ctx.TokenBudget;
            }

        }

        yield return End(cwd, EndReason.Cancelled);
    }

    private sealed record ToolCallRepetition(
        List<string> SignatureList,
        HashSet<string> Signatures,
        bool IsDuplicate,
        bool IsRepeating);

    private static List<LoopEvent> ApplyToolLoopGuard(
        LoopContext ctx,
        TurnResponse response,
        bool isDuplicate,
        int attempt)
    {
        // Escalate: the first hint nudges, later ones become an imperative demand to act, since a
        // weak model that ignored the gentle hint needs a firmer push before the loop gives up.
        var hint = attempt >= 2
            ? "You are STILL repeating the same reads/searches and have made no edits. "
              + "Stop reading immediately. Your very next action MUST be an Edit or Write tool call "
              + "that changes a file. Do NOT call Read, Glob, Grep, or List again."
            : isDuplicate
                ? "You are repeating the exact same tool calls as the previous turn. "
                  + "Do not use any tools. Synthesize the information already returned and respond to the user in plain text."
                : "You keep repeating the same reads and searches without making progress. "
                  + "Stop gathering information. Either make a concrete change with Edit or Write, "
                  + "or respond in plain text with what you found or why you are blocked.";
        var events = new List<LoopEvent>(response.ToolCalls.Count);
        var blocks = new List<ContentBlock>(response.ToolCalls.Count);
        for (var i = 0; i < response.ToolCalls.Count; i++)
        {
            var (id, name, _) = response.ToolCalls[i];
            var index = response.PendingToolIndex.TryGetValue(id, out var toolIndex) ? toolIndex : i;
            events.Add(new ToolFinished(index, name, ToolResult.Ok("[skipped: loop guard]")));
            blocks.Add(new ToolResultBlock(id, hint, false));
        }
        ctx.Messages.Add(new UserMessage(blocks));
        return events;
    }

    private async Task<(ExecuteToolsResult Result, int ConsecutiveErrorTurns, List<LoopEvent> Events)>
        ExecuteToolTurnAsync(
            LoopContext ctx,
            string cwd,
            TurnResponse response,
            int consecutiveErrorTurns,
            CancellationToken ct)
    {
        var result = await ExecuteTools(ctx, cwd, response.ToolCalls, ct);
        var events = new List<LoopEvent>(response.ToolCalls.Count);
        var resultBlocks = new List<ContentBlock>(response.ToolCalls.Count);
        for (var i = 0; i < response.ToolCalls.Count; i++)
        {
            var (id, name, _) = response.ToolCalls[i];
            var toolResult = result.Results[i].Result;
            var index = response.PendingToolIndex.TryGetValue(id, out var toolIndex) ? toolIndex : i;
            events.Add(new ToolFinished(index, name, toolResult));
            resultBlocks.Add(new ToolResultBlock(id, toolResult.Content, toolResult.IsError));
        }
        ctx.Messages.Add(new UserMessage(resultBlocks));

        if (result.Results.Length > 0 && result.Results.All(run => run.Result.IsError))
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

        AppendToolResultSession(cwd, response.ToolCalls, result);
        return (result, consecutiveErrorTurns, events);
    }

    private ToolCallRepetition DetectToolCallRepetition(
        List<(string Id, string Name, string Args)> toolCalls,
        HashSet<string>? previousSignatures,
        Queue<List<string>> recentTurnSignatures)
    {
        var signatureList = toolCalls.Select(call => $"{call.Name}:{call.Args.Trim()}").ToList();
        var signatures = new HashSet<string>(signatureList, StringComparer.Ordinal);
        var isDuplicate = previousSignatures is not null
            && signatures.Count > 0
            && signatures.SetEquals(previousSignatures);

        var isRepeating = false;
        if (!isDuplicate && config.Agent.RepeatThreshold > 1 && signatures.Count > 0)
        {
            var windowCounts = recentTurnSignatures
                .SelectMany(turn => turn)
                .GroupBy(signature => signature, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            isRepeating = signatures.All(signature =>
                windowCounts.TryGetValue(signature, out var count)
                && count + 1 >= config.Agent.RepeatThreshold);
        }

        return new ToolCallRepetition(signatureList, signatures, isDuplicate, isRepeating);
    }

    private void AppendToolResultSession(
        string cwd,
        List<(string Id, string Name, string Args)> toolCalls,
        ExecuteToolsResult execution)
    {
        if (sessionStore is null)
            return;

        var parts = new List<object>(toolCalls.Count);
        for (var i = 0; i < toolCalls.Count; i++)
        {
            var (id, name, _) = toolCalls[i];
            var result = execution.Results[i];
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

    private async IAsyncEnumerable<LoopEvent> StreamResponseAsync(
        ChatRequest request,
        LoopContext ctx,
        string cwd,
        TurnResponse response,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var providerEvent in provider.StreamAsync(request, ct))
        {
            switch (providerEvent)
            {
                case TextDelta text:
                    response.Text.Append(text.Text);
                    yield return new TextChunk(text.Text);
                    break;

                case ThinkingDelta thinking:
                    response.Thinking.Append(thinking.Text);
                    yield return new ThinkingChunk(thinking.Text);
                    break;

                case ToolCallDelta toolCall:
                    response.ToolCalls.Add((toolCall.Id, toolCall.Name, toolCall.ArgumentsJson));
                    yield return new ToolStarted(
                        response.NextToolEventIndex,
                        toolCall.Name,
                        toolCall.ArgumentsJson);
                    response.PendingToolIndex[toolCall.Id] = response.NextToolEventIndex++;
                    break;

                case UsageUpdate usage:
                    response.Budget = response.Budget.WithUsed(usage.InputTokens + usage.OutputTokens);
                    ctx.TokenBudget = response.Budget;
                    trajectoryRecorder?.RecordUsage(usage);
                    response.InputTokens = usage.InputTokens;
                    response.OutputTokens = usage.OutputTokens;
                    response.CacheReadTokens = usage.CacheReadTokens;
                    response.CacheWriteTokens = usage.CacheWriteTokens;
                    yield return new TokenUsageUpdated(usage);
                    break;

                case StreamEnd streamEnd:
                    response.StopReason = streamEnd.Reason;
                    break;

                case StreamError streamError:
                    await foreach (var errorEvent in HandleStreamErrorAsync(cwd, response, streamError.Ex, ct))
                        yield return errorEvent;
                    break;
            }

            if (response.HadError)
                yield break;
        }
    }

    private async IAsyncEnumerable<LoopEvent> HandleStreamErrorAsync(
        string cwd,
        TurnResponse response,
        Exception exception,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (IsContextLengthError(exception))
        {
            response.ContextLengthError = exception;
        }
        else if (exception is ProviderException { Error: ModelUnknownError modelError })
        {
            var models = await provider.GetModelsAsync(ct);
            var modelList = string.Join("\n", models.Select(model => $"- {model.Id}"));
            yield return End(
                cwd,
                EndReason.Error,
                $"Model unknown: {modelError.Message}\n\nAvailable models for {ProviderConfig.GetProviderDisplayName(provider.Name)}:\n{modelList}");
        }
        else
        {
            yield return End(
                cwd,
                EndReason.Error,
                $"Error while invoking {ProviderConfig.GetProviderDisplayName(provider.Name)} API\n{exception.Message}");
        }

        response.HadError = true;
    }

    private void AppendAssistantTurn(LoopContext ctx, string cwd, TurnResponse response, ChatRequest request)
    {
        var assistantBlocks = new List<ContentBlock>();
        if (response.Text.Length > 0)
            assistantBlocks.Add(new TextBlock(response.Text.ToString()));
        if (response.Thinking.Length > 0)
            assistantBlocks.Add(new ThinkingBlock(response.Thinking.ToString()));
        foreach (var (id, name, args) in response.ToolCalls)
            assistantBlocks.Add(new ToolUseBlock(id, name, ToolArgs.TryParseArgs(args)));
        ctx.Messages.Add(new AssistantMessage(assistantBlocks));

        if (sessionStore is null || (response.Text.Length == 0 && response.ToolCalls.Count == 0))
            return;

        object message;
        if (response.ToolCalls.Count > 0)
        {
            var contentParts = new List<object>();
            if (response.Text.Length > 0)
                contentParts.Add(new { type = "text", text = response.Text.ToString() });
            foreach (var (id, name, args) in response.ToolCalls)
                contentParts.Add(new { type = "tool_use", id, name, input = args });
            message = new { content = contentParts };
        }
        else
        {
            message = new { content = response.Text.ToString() };
        }

        sessionStore.Append(new SessionRecord
        {
            Type = SessionRecordType.Assistant,
            Cwd = cwd,
            Message = message,
            Usage = response.InputTokens > 0 || response.OutputTokens > 0
                ? new SessionUsage
                {
                    InputTokens = response.InputTokens,
                    OutputTokens = response.OutputTokens,
                    CacheReadTokens = response.CacheReadTokens,
                    CacheWriteTokens = response.CacheWriteTokens,
                    ContextWindowTokens = response.Budget.ContextWindow,
                    MaxOutputTokens = request.MaxTokens,
                    ReserveTokens = response.Budget.ReserveTokens,
                    UsedTokens = response.Budget.UsedTokens
                }
                : null
        });
    }

    /// <summary>
    /// Records the reason the loop terminated into the session log, then returns the event to
    /// yield. Routing every terminal LoopEnded through here keeps the JSONL diagnosable (e.g.
    /// telling a clean Done from a nudge-limit bail-out, which both render as "idle" in the TUI).
    /// </summary>
    /// <param name="reason"></param>
    /// <param name="message"></param>
    /// <returns></returns>
    private LoopEnded End(string cwd, EndReason reason, string? message = null)
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

            // Track the outcome of build/test commands so the completion guard can refuse a Done
            // signal issued over a failing build.
            if (AgentLoopHeuristics.LooksLikeBuildCommand(name, args))
                ctx.LastBuildFailed = results[i].Result.IsError;

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

        var input = ToolArgs.TryParseArgs(args);
        return input.TryGetProperty("path", out var pathEl) ? pathEl.GetString() : null;
    }

    // Returns a de-dupe stub for a repeat Read whose content is still live in context, else null.
    private static string? TryReadDedupeStub(LoopContext ctx, string cwd, System.Text.Json.JsonElement args)
    {
        if (!TryResolveReadTarget(cwd, args, out var path, out var mtime, out var size, out var offset, out var limit))
            return null;
        // Snapshot messages under the same lock the summarizer uses, to read them safely while a
        // background summarize may be mutating the list.
        List<Message> snapshot;
        lock (ctx.Messages) snapshot = new List<Message>(ctx.Messages);
        return ReadDedup.StubForRepeatRead(ctx.ReadCache, snapshot, path, mtime, size, offset, limit);
    }

    private static void RememberRead(LoopContext ctx, string cwd, System.Text.Json.JsonElement args, string content)
    {
        if (TryResolveReadTarget(cwd, args, out var path, out var mtime, out var size, out var offset, out var limit))
            ctx.ReadCache[path] = new ReadCacheEntry(mtime, size, offset, limit, content);
    }

    private static bool TryResolveReadTarget(
        string cwd, System.Text.Json.JsonElement args,
        out string path, out long mtime, out long size, out int offset, out int limit)
    {
        path = ""; mtime = 0; size = 0; offset = 0; limit = 0;
        try
        {
            path = ReadTool.ResolvePath(args, cwd);
            var fi = new System.IO.FileInfo(path);
            if (!fi.Exists) return false;
            mtime = fi.LastWriteTimeUtc.Ticks;
            size = fi.Length;
            (offset, limit) = ParseReadRange(args);
            return true;
        }
        catch { return false; }
    }

    // Mirrors ReadTool's offset/limit resolution so the cache key matches what was actually read.
    private static (int Offset, int Limit) ParseReadRange(System.Text.Json.JsonElement input)
    {
        int? startLine = FlexInt(input, "start_line");
        int? endLine = FlexInt(input, "end_line");
        int offset = FlexInt(input, "offset") ?? (startLine.HasValue ? Math.Max(0, startLine.Value - 1) : 0);
        int limit = FlexInt(input, "limit")
            ?? (startLine.HasValue && endLine.HasValue ? Math.Max(1, endLine.Value - startLine.Value + 1) : 2000);
        return (offset, Math.Min(limit, 2000));
    }

    private static int? FlexInt(System.Text.Json.JsonElement o, string key)
    {
        if (!o.TryGetProperty(key, out var v)) return null;
        if (v.ValueKind == System.Text.Json.JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == System.Text.Json.JsonValueKind.String && int.TryParse(v.GetString(), out var s)) return s;
        return null;
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
                        // For a write outside cwd, also remember its project (repo) root so sibling
                        // files there aren't re-prompted — the in-cwd allowance above never covers them.
                        permissions.AllowWriteRootForOutside(toolName, args);
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
        var argsElement = ToolArgs.TryParseArgs(args);

        // Read de-duplication: skip re-reading a file whose content is still live in context.
        if (config.Agent.DedupeReads && string.Equals(toolName, ReadTool.ToolName, StringComparison.Ordinal))
        {
            var stub = TryReadDedupeStub(ctx, cwd, argsElement);
            if (stub is not null)
                return new ToolRunResult(ToolResult.Ok(stub), 0, 0);
        }

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
            if (config.Agent.DedupeReads && !result.IsError
                && string.Equals(toolName, ReadTool.ToolName, StringComparison.Ordinal))
                RememberRead(ctx, cwd, argsElement, result.Content);
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

    private async Task<(LoopEvent? Event, bool ShouldContinue)> ProcessReflectionAsync(
        LoopContext ctx,
        string cwd,
        bool anyWriteTools,
        CancellationToken ct)
    {
        if (!anyWriteTools || (!config.Agent.AutoLint && !config.Agent.AutoTest))
            return (null, false);

        var reflection = await RunReflection(ctx, cwd, ct);
        if (reflection is null)
        {
            ctx.Reflections = 0;
            return (null, false);
        }

        if (ctx.Reflections >= config.Agent.MaxReflections)
        {
            ctx.Reflections = 0;
            return (
                new ReflectionOccurred($"Max reflections ({config.Agent.MaxReflections}) reached"),
                false);
        }

        ctx.Reflections++;
        ctx.Messages.Add(new UserMessage([new TextBlock(reflection)]));
        return (new ReflectionOccurred(reflection), true);
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
            config.Model.ActiveModel.Id,
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


    private void GitCheckpoint(string cwd, LoopContext ctx, StringBuilder textBuilder, 
        int turn, IReadOnlyList<string>? affectedPaths)
    {
        var git = new GitIntegration(cwd);
        if (config.Agent.AutoCommit)
        {
            var firstLine = textBuilder.ToString()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? "turn complete";
            git.AutoCommit(ctx.SessionId, turn, firstLine, affectedPaths);
        }
        git.WriteCheckpoint(ctx.SessionId, turn);
    }
}
