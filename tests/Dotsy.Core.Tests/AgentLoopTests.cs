using System.Diagnostics;
using System.Text.Json;
using Dotsy.Core.Config;
using Dotsy.Core.Git;
using Dotsy.Core.Loop;
using Dotsy.Core.Loop.Data;
using Dotsy.Core.Providers;
using Dotsy.Core.Session;
using Dotsy.Core.Tests.Helpers;
using Dotsy.Core.Tools;

namespace Dotsy.Core.Tests;

[TestClass]
public sealed class AgentLoopTests
{
    private static DotsyConfig MakeConfig(
        int maxTurns = 1000, int nudgeLimit = 3,
        bool autoContinue = false, int autoContinueMax = 3,
        bool autoContinueEndTurnIntent = false) =>
        new()
        {
            Model    = new ModelConfig { Anthropic = new() { Id = "test-model" }, MaxOutputTokensPerRequest = 1024 },
            Agent    = new AgentConfig
            {
                MaxTurns = maxTurns, NudgeLimit = nudgeLimit, ParallelTools = true,
                AutoLint = false, AutoTest = false, MaxReflections = 0,
                InjectEnvironment = false, InjectGitStatus = false,
                AutoContinueOnNudge = autoContinue, AutoContinueMaxAttempts = autoContinueMax,
                AutoContinueOnEndTurnIntent = autoContinueEndTurnIntent
            },
            Compaction  = new CompactionConfig { Enabled = false },
            Retrieval   = new RetrievalConfig  { RepoMapTokens = 0 },
            Skills      = new SkillsConfig     { Paths = [] },
            Git         = new GitConfig(),
            Tui         = new TuiConfig(),
            Permissions = new PermissionsConfig { AlwaysAllow = [], NeverAllow = [] },
            Session     = new SessionConfig()
        };

    private static DotsyConfig MakeCompactionConfig() =>
        new()
        {
            Model = new ModelConfig { Anthropic = new() { Id = "test-model" }, MaxOutputTokensPerRequest = 1024 },
            Agent = new AgentConfig
            {
                MaxTurns = 1, NudgeLimit = 1, ParallelTools = true,
                AutoLint = false, AutoTest = false, MaxReflections = 0,
                InjectEnvironment = false, InjectGitStatus = false,
                AutoContinueOnNudge = false
            },
            Compaction = new CompactionConfig
            {
                Enabled = true,
                ThresholdPct = 0.80f,
                ReserveTokens = 10,
                KeepRecentTokens = 1
            },
            Retrieval = new RetrievalConfig { RepoMapTokens = 0 },
            Skills = new SkillsConfig { Paths = [] },
            Git = new GitConfig(),
            Tui = new TuiConfig(),
            Permissions = new PermissionsConfig { AlwaysAllow = [], NeverAllow = [] },
            Session = new SessionConfig()
        };

    private static PermissionStore YoloStore() =>
        new(new PermissionsConfig { AlwaysAllow = [], NeverAllow = [] }, Path.GetTempPath())
        { Yolo = true };

    private static LoopContext EmptyCtx() => new()
    {
        TokenBudget = new TokenBudget(200_000, 16_384, 20_000, 0, 0.9f)
    };

    private static IReadOnlyList<ProviderEvent> TextTurn(string text = "hello") =>
        [new TextDelta(text), new StreamEnd(StopReason.EndTurn)];

    private static IReadOnlyList<ProviderEvent> TruncatedTextTurn(string text = "hello") =>
        [new TextDelta(text), new StreamEnd(StopReason.MaxTokens)];

    // Model signalled tool use but emitted no parseable tool call and no text — a stalled,
    // no-progress turn (common with local models producing malformed tool syntax).
    private static IReadOnlyList<ProviderEvent> EmptyToolUseTurn() =>
        [new StreamEnd(StopReason.ToolUse)];

    private static IReadOnlyList<ProviderEvent> ModelUnknownTurn(string message = "invalid model ID") =>
        [new StreamError(new ProviderException(new ModelUnknownError(message)))];

    private static async Task<List<LoopEvent>> Collect(IAsyncEnumerable<LoopEvent> src)
    {
        var list = new List<LoopEvent>();
        await foreach (var ev in src)
            list.Add(ev);
        return list;
    }

    // ── Nudge limit ───────────────────────────────────────────────────────────

    [TestMethod]
    public async Task ModelUnknownError_EndsWithAvailableModels()
    {
        var config = MakeConfig();
        var provider = new FakeProvider(ModelUnknownTurn());
        var loop = new AgentLoop(provider, new ToolRegistry(), YoloStore(), config);

        var events = await Collect(loop.RunAsync(EmptyCtx(), Path.GetTempPath(), CancellationToken.None));

        var ended = events.OfType<LoopEnded>().Single();
        Assert.AreEqual(EndReason.Error, ended.Reason);
        StringAssert.Contains(ended.Message, "Model unknown: invalid model ID");
        StringAssert.Contains(ended.Message, "Available models for fake:");
        StringAssert.Contains(ended.Message, "- fake");
    }

    [TestMethod]
    public async Task TextOnlyEndTurn_CompletesWithoutAnotherProviderRequest()
    {
        var config = MakeConfig(maxTurns: 100, nudgeLimit: 3);
        var provider = new FakeProvider(TextTurn());
        var loop = new AgentLoop(provider, new ToolRegistry(), YoloStore(), config);

        var events = await Collect(loop.RunAsync(EmptyCtx(), Path.GetTempPath(), CancellationToken.None));

        var ended = events.OfType<LoopEnded>().Single();
        Assert.AreEqual(EndReason.ResponseComplete, ended.Reason);
        Assert.AreEqual(1, provider.CallCount);
    }

    [TestMethod]
    public async Task NudgeTriggers_AfterConsecutiveNonTerminalNoToolTurns()
    {
        var config   = MakeConfig(maxTurns: 100, nudgeLimit: 2);
        var provider = new FakeProvider(TruncatedTextTurn()); // same sequence reused
        var registry = new ToolRegistry();
        var loop     = new AgentLoop(provider, registry, YoloStore(), config);

        var events = await Collect(loop.RunAsync(EmptyCtx(), Path.GetTempPath(), CancellationToken.None));

        var ended = events.OfType<LoopEnded>().LastOrDefault();
        Assert.IsNotNull(ended, "Expected a LoopEnded event");
        Assert.AreEqual(EndReason.MaxTokens, ended.Reason);
        Assert.AreEqual(2, provider.CallCount, "Provider called once per nudge turn");
    }

    [TestMethod]
    public async Task NoProgressStall_EndsWithNoProgress_WhenAutoContinueDisabled()
    {
        // No text, no tool call, non-terminal stop: a genuine no-progress stall.
        var config   = MakeConfig(maxTurns: 100, nudgeLimit: 2, autoContinue: false);
        var provider = new FakeProvider(EmptyToolUseTurn());
        var loop     = new AgentLoop(provider, new ToolRegistry(), YoloStore(), config);

        var events = await Collect(loop.RunAsync(EmptyCtx(), Path.GetTempPath(), CancellationToken.None));

        var ended = events.OfType<LoopEnded>().LastOrDefault();
        Assert.IsNotNull(ended);
        Assert.AreEqual(EndReason.NoProgress, ended.Reason);
    }

    // ── Auto-continue recovery ────────────────────────────────────────────────

    [TestMethod]
    public async Task AutoContinue_InjectsHintAndRecovers_WhenModelResumes()
    {
        // Turn 1 stalls (no progress), auto-continue injects a hint, turn 2 completes cleanly.
        var config   = MakeConfig(maxTurns: 100, nudgeLimit: 1, autoContinue: true, autoContinueMax: 3);
        var provider = new FakeProvider(EmptyToolUseTurn(), TextTurn("all done"));
        var loop     = new AgentLoop(provider, new ToolRegistry(), YoloStore(), config);

        var events = await Collect(loop.RunAsync(EmptyCtx(), Path.GetTempPath(), CancellationToken.None));

        var ended = events.OfType<LoopEnded>().Single();
        Assert.AreEqual(EndReason.ResponseComplete, ended.Reason);
        Assert.AreEqual(2, provider.CallCount);

        var autoContinued = events.OfType<AutoContinued>().ToList();
        Assert.AreEqual(1, autoContinued.Count, "Exactly one recovery attempt");
        Assert.AreEqual(1, autoContinued[0].Attempt);

        // The recovery hint must be present in the second request's messages.
        var secondRequestText = string.Join("\n", provider.Requests[1].Messages
            .OfType<UserMessage>()
            .SelectMany(m => m.Content.OfType<TextBlock>())
            .Select(t => t.Text));
        StringAssert.Contains(secondRequestText, "did not make progress");
    }

    [TestMethod]
    public async Task AutoContinue_ExhaustsAttempts_ThenEndsWithSpecificReason()
    {
        // Model never recovers: auto-continue fires up to the cap, then the loop ends reporting
        // the underlying cause (truncated output here).
        var config   = MakeConfig(maxTurns: 100, nudgeLimit: 1, autoContinue: true, autoContinueMax: 2);
        var provider = new FakeProvider(TruncatedTextTurn());
        var loop     = new AgentLoop(provider, new ToolRegistry(), YoloStore(), config);

        var events = await Collect(loop.RunAsync(EmptyCtx(), Path.GetTempPath(), CancellationToken.None));

        var ended = events.OfType<LoopEnded>().Single();
        Assert.AreEqual(EndReason.MaxTokens, ended.Reason);
        Assert.AreEqual(2, events.OfType<AutoContinued>().Count(), "Retried exactly max attempts");
        Assert.AreEqual(3, provider.CallCount, "1 initial + 2 retries");
    }

    [TestMethod]
    public async Task AutoContinue_ProgressResetsRetryBudget()
    {
        // Progress guard: a successful tool call between stalls resets the retry budget, so the
        // agent can recover more than max-attempts times as long as it keeps making progress.
        var config   = MakeConfig(maxTurns: 100, nudgeLimit: 1, autoContinue: true, autoContinueMax: 1);
        var provider = new FakeProvider(
            TruncatedTextTurn(),                                                             // stall -> attempt 1
            [new ToolCallDelta("t1", "Look", """{"p":"a"}"""), new StreamEnd(StopReason.ToolUse)], // progress -> resets
            TruncatedTextTurn(),                                                             // stall -> attempt 1 again
            TextTurn("done"));                                                               // completes
        var registry = new ToolRegistry();
        registry.Register(new AlwaysOkTool());
        var loop = new AgentLoop(provider, registry, YoloStore(), config);

        var events = await Collect(loop.RunAsync(EmptyCtx(), Path.GetTempPath(), CancellationToken.None));

        var ended = events.OfType<LoopEnded>().Single();
        Assert.AreEqual(EndReason.ResponseComplete, ended.Reason);
        Assert.AreEqual(2, events.OfType<AutoContinued>().Count(),
            "Two recoveries despite max-attempts=1, because progress reset the budget");
    }

    // ── Auto-continue on announced-but-unacted next step (text-only EndTurn) ──

    private static IReadOnlyList<ProviderEvent> AnnouncedNextStepTurn() =>
        [new TextDelta("I have enough context from prior reads. Let me implement the feature now."),
         new StreamEnd(StopReason.EndTurn)];

    [TestMethod]
    public async Task AnnouncedNextStep_EndTurn_AutoContinuesInsteadOfStopping()
    {
        // Turn 1 is a clean EndTurn but only *announces* the next action (no tool call). With the
        // guard enabled the loop nudges and retries; turn 2 completes.
        var config   = MakeConfig(maxTurns: 100, autoContinueEndTurnIntent: true, autoContinueMax: 3);
        var provider = new FakeProvider(AnnouncedNextStepTurn(), TextTurn("all done"));
        var loop     = new AgentLoop(provider, new ToolRegistry(), YoloStore(), config);

        var events = await Collect(loop.RunAsync(EmptyCtx(), Path.GetTempPath(), CancellationToken.None));

        var ended = events.OfType<LoopEnded>().Single();
        Assert.AreEqual(EndReason.ResponseComplete, ended.Reason);
        Assert.AreEqual(2, provider.CallCount, "Announced-intent turn should have triggered a retry");

        var autoContinued = events.OfType<AutoContinued>().Single();
        Assert.AreEqual(1, autoContinued.Attempt);

        var secondRequestText = string.Join("\n", provider.Requests[1].Messages
            .OfType<UserMessage>()
            .SelectMany(m => m.Content.OfType<TextBlock>())
            .Select(t => t.Text));
        StringAssert.Contains(secondRequestText, "did not take it");
    }

    [TestMethod]
    public async Task AnnouncedNextStep_Disabled_EndsImmediately()
    {
        // Same announced-intent turn, but the guard is off: legacy behaviour (end immediately).
        var config   = MakeConfig(maxTurns: 100, autoContinueEndTurnIntent: false);
        var provider = new FakeProvider(AnnouncedNextStepTurn());
        var loop     = new AgentLoop(provider, new ToolRegistry(), YoloStore(), config);

        var events = await Collect(loop.RunAsync(EmptyCtx(), Path.GetTempPath(), CancellationToken.None));

        Assert.AreEqual(EndReason.ResponseComplete, events.OfType<LoopEnded>().Single().Reason);
        Assert.AreEqual(1, provider.CallCount);
        Assert.AreEqual(0, events.OfType<AutoContinued>().Count());
    }

    [TestMethod]
    public async Task PlainFinalAnswer_EndTurn_EndsWithoutRetry_EvenWhenGuardEnabled()
    {
        // A genuine final answer (no announced next action, and a closing pleasantry that merely
        // contains "let me know") must not be mistaken for a stall.
        var config   = MakeConfig(maxTurns: 100, autoContinueEndTurnIntent: true);
        var provider = new FakeProvider(TextTurn("Done. Let me know if you need anything else."));
        var loop     = new AgentLoop(provider, new ToolRegistry(), YoloStore(), config);

        var events = await Collect(loop.RunAsync(EmptyCtx(), Path.GetTempPath(), CancellationToken.None));

        Assert.AreEqual(EndReason.ResponseComplete, events.OfType<LoopEnded>().Single().Reason);
        Assert.AreEqual(1, provider.CallCount, "A final answer must not trigger a retry");
        Assert.AreEqual(0, events.OfType<AutoContinued>().Count());
    }

    [TestMethod]
    public async Task AnnouncedNextStep_ExhaustsBudget_ThenEndsResponseComplete()
    {
        // The model keeps announcing without ever acting: retries up to the cap, then stops.
        var config   = MakeConfig(maxTurns: 100, autoContinueEndTurnIntent: true, autoContinueMax: 2);
        var provider = new FakeProvider(AnnouncedNextStepTurn());
        var loop     = new AgentLoop(provider, new ToolRegistry(), YoloStore(), config);

        var events = await Collect(loop.RunAsync(EmptyCtx(), Path.GetTempPath(), CancellationToken.None));

        Assert.AreEqual(EndReason.ResponseComplete, events.OfType<LoopEnded>().Single().Reason);
        Assert.AreEqual(2, events.OfType<AutoContinued>().Count(), "Retried exactly max attempts");
        Assert.AreEqual(3, provider.CallCount, "1 initial + 2 retries");
    }

    [TestMethod]
    public async Task AnnouncedNextStep_ThenToolCall_ResetsBudget()
    {
        // Progress guard: acting (a tool call) after an announced-intent stall resets the retry
        // budget, so a later stall can recover again even with max-attempts=1.
        var config   = MakeConfig(maxTurns: 100, autoContinueEndTurnIntent: true, autoContinueMax: 1);
        var provider = new FakeProvider(
            AnnouncedNextStepTurn(),                                                           // stall -> attempt 1
            [new ToolCallDelta("t1", "Look", """{"p":"a"}"""), new StreamEnd(StopReason.ToolUse)], // acts -> resets
            AnnouncedNextStepTurn(),                                                           // stall -> attempt 1 again
            TextTurn("done"));                                                                 // completes
        var registry = new ToolRegistry();
        registry.Register(new AlwaysOkTool());
        var loop = new AgentLoop(provider, registry, YoloStore(), config);

        var events = await Collect(loop.RunAsync(EmptyCtx(), Path.GetTempPath(), CancellationToken.None));

        Assert.AreEqual(EndReason.ResponseComplete, events.OfType<LoopEnded>().Single().Reason);
        Assert.AreEqual(2, events.OfType<AutoContinued>().Count(),
            "Two recoveries despite max-attempts=1, because acting reset the budget");
    }

    // ── Headless: model asks the user a question with nobody to answer ────────

    private static IReadOnlyList<ProviderEvent> AsksUserTurn() =>
        [new TextDelta("The request seems cut off. Could you clarify what you want, and what's your name?"),
         new StreamEnd(StopReason.EndTurn)];

    [TestMethod]
    public async Task HeadlessQuestionToUser_AutoContinuesInsteadOfStopping()
    {
        var config = MakeConfig(maxTurns: 100, autoContinueEndTurnIntent: true, autoContinueMax: 3);
        config.Agent.Headless = true;
        var provider = new FakeProvider(AsksUserTurn(), TextTurn("done"));
        var loop = new AgentLoop(provider, new ToolRegistry(), YoloStore(), config);

        var events = await Collect(loop.RunAsync(EmptyCtx(), Path.GetTempPath(), CancellationToken.None));

        Assert.AreEqual(EndReason.ResponseComplete, events.OfType<LoopEnded>().Single().Reason);
        Assert.AreEqual(2, provider.CallCount, "Question in headless mode should trigger a retry");
        var ac = events.OfType<AutoContinued>().Single();
        Assert.AreEqual(1, ac.Attempt);
        var secondReq = string.Join("\n", provider.Requests[1].Messages
            .OfType<UserMessage>().SelectMany(m => m.Content.OfType<TextBlock>()).Select(t => t.Text));
        StringAssert.Contains(secondReq, "no user available");
    }

    [TestMethod]
    public async Task QuestionToUser_Interactive_EndsWithoutRetry()
    {
        // Same question, but NOT headless: a user could answer, so ending (yielding to them) is right.
        var config = MakeConfig(maxTurns: 100, autoContinueEndTurnIntent: true);
        config.Agent.Headless = false;
        var provider = new FakeProvider(AsksUserTurn());
        var loop = new AgentLoop(provider, new ToolRegistry(), YoloStore(), config);

        var events = await Collect(loop.RunAsync(EmptyCtx(), Path.GetTempPath(), CancellationToken.None));

        Assert.AreEqual(EndReason.ResponseComplete, events.OfType<LoopEnded>().Single().Reason);
        Assert.AreEqual(1, provider.CallCount);
        Assert.AreEqual(0, events.OfType<AutoContinued>().Count());
    }

    // ── MaxTurns ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task MaxTurns_YieldsLoopEndedTurnLimitReached()
    {
        var config   = MakeConfig(maxTurns: 2, nudgeLimit: 100);
        var provider = new FakeProvider(TruncatedTextTurn());
        var registry = new ToolRegistry();
        var loop     = new AgentLoop(provider, registry, YoloStore(), config);

        var events = await Collect(loop.RunAsync(EmptyCtx(), Path.GetTempPath(), CancellationToken.None));

        var ended = events.OfType<LoopEnded>().LastOrDefault();
        Assert.IsNotNull(ended);
        Assert.AreEqual(EndReason.TurnLimitReached, ended.Reason);
        Assert.AreEqual(2, events.OfType<TurnComplete>().Count(), "Exactly 2 turns ran");
    }

    // ── Failing-tool loop guard ───────────────────────────────────────────────

    // A ReadOnly tool that always fails, like Read against a path that doesn't exist.
    private sealed class AlwaysFailsTool : Dotsy.Core.Tools.Interfaces.ITool
    {
        public string Name => "FailRead";
        public string Description => "always fails";
        public System.Text.Json.JsonElement InputSchema =>
            System.Text.Json.JsonSerializer.SerializeToElement(new { type = "object" });
        public ToolSafety Safety => ToolSafety.ReadOnly;
        public bool IsCompletionSignal => false;
        public Task<ToolResult> ExecuteAsync(System.Text.Json.JsonElement input, ToolContext ctx, CancellationToken ct)
            => Task.FromResult(ToolResult.Err("File not found"));
    }

    [TestMethod]
    public async Task AllErrorTurns_BreakLoop_EvenWhenFailingArgsVary()
    {
        // nudgeLimit high (every turn has tool calls anyway) and args differ each turn so the
        // exact-duplicate trap never fires — only the consecutive-all-errors guard can stop this.
        var config = MakeConfig(maxTurns: 1000, nudgeLimit: 1000);
        var turns = new IReadOnlyList<ProviderEvent>[6];
        for (int i = 0; i < turns.Length; i++)
            turns[i] = [new ToolCallDelta($"c{i}", "FailRead", $$"""{"path":"guess{{i}}.cs"}"""), new StreamEnd(StopReason.ToolUse)];

        var provider = new FakeProvider(turns);
        var registry = new ToolRegistry();
        registry.Register(new AlwaysFailsTool());
        var loop = new AgentLoop(provider, registry, YoloStore(), config);

        var events = await Collect(loop.RunAsync(EmptyCtx(), Path.GetTempPath(), CancellationToken.None));

        var ended = events.OfType<LoopEnded>().LastOrDefault();
        Assert.IsNotNull(ended, "Expected the loop to stop");
        Assert.AreEqual(EndReason.ToolErrorStreak, ended.Reason);
        Assert.IsTrue(provider.CallCount <= 5, $"Loop should bail quickly, made {provider.CallCount} calls");
    }

    // ── Completion guard: refuse Done over a failing build ────────────────────

    // A Shell-named tool that fails (like a non-zero build) when its command contains "fail",
    // otherwise succeeds. Named "Shell" so LooksLikeBuildCommand recognizes its "dotnet build".
    private sealed class FakeShellTool : Dotsy.Core.Tools.Interfaces.ITool
    {
        public string Name => ShellTool.ToolName;
        public string Description => "fake shell";
        public System.Text.Json.JsonElement InputSchema =>
            System.Text.Json.JsonSerializer.SerializeToElement(new { type = "object" });
        public ToolSafety Safety => ToolSafety.Sequential;
        public bool IsCompletionSignal => false;
        public Task<ToolResult> ExecuteAsync(System.Text.Json.JsonElement input, ToolContext ctx, CancellationToken ct)
        {
            var cmd = input.TryGetProperty("command", out var c) ? c.GetString() ?? "" : "";
            return Task.FromResult(cmd.Contains("fail", StringComparison.OrdinalIgnoreCase)
                ? ToolResult.Err("Exit code 1\nbuild failed")
                : ToolResult.Ok("Build succeeded."));
        }
    }

    // A completion-signal tool, like Done.
    private sealed class FakeDoneTool : Dotsy.Core.Tools.Interfaces.ITool
    {
        public string Name => "Done";
        public string Description => "signals completion";
        public System.Text.Json.JsonElement InputSchema =>
            System.Text.Json.JsonSerializer.SerializeToElement(new { type = "object" });
        public ToolSafety Safety => ToolSafety.Sequential;
        public bool IsCompletionSignal => true;
        public Task<ToolResult> ExecuteAsync(System.Text.Json.JsonElement input, ToolContext ctx, CancellationToken ct)
            => Task.FromResult(ToolResult.Ok("done"));
    }

    private static IReadOnlyList<ProviderEvent> ShellTurn(string id, string command) =>
        [new ToolCallDelta(id, ShellTool.ToolName, $$"""{"command":"{{command}}"}"""), new StreamEnd(StopReason.ToolUse)];

    // Args vary per call so identical Done signatures don't trip the exact-duplicate loop trap —
    // this test isolates the completion guard.
    private static IReadOnlyList<ProviderEvent> DoneTurn(string id = "d") =>
        [new ToolCallDelta(id, "Done", $$"""{"n":"{{id}}"}"""), new StreamEnd(StopReason.ToolUse)];

    private static (AgentLoop, FakeProvider) BuildGuardLoop(bool verify, params IReadOnlyList<ProviderEvent>[] turns)
    {
        var config = MakeConfig(maxTurns: 100, nudgeLimit: 1000, autoContinueMax: 3);
        config.Agent.VerifyBuildBeforeComplete = verify;
        var provider = new FakeProvider(turns);
        var registry = new ToolRegistry();
        registry.Register(new FakeShellTool());
        registry.Register(new FakeDoneTool());
        return (new AgentLoop(provider, registry, YoloStore(), config), provider);
    }

    [TestMethod]
    public async Task CompletionOverFailingBuild_IsRefused_ThenRecovers()
    {
        // Done is signalled while the last build failed → guard refuses it and injects a hint.
        // The model then re-runs a passing build and signals Done again → completes.
        var (loop, provider) = BuildGuardLoop(verify: true,
            ShellTurn("s1", "dotnet build # fail"),   // build FAILS
            DoneTurn("d1"),                            // premature Done -> refused
            ShellTurn("s2", "dotnet build"),           // build PASSES -> clears flag
            DoneTurn("d2"));                           // accepted

        var events = await Collect(loop.RunAsync(EmptyCtx(), Path.GetTempPath(), CancellationToken.None));

        var ended = events.OfType<LoopEnded>().Single();
        Assert.AreEqual(EndReason.TaskComplete, ended.Reason);
        Assert.AreEqual(1, events.OfType<AutoContinued>().Count(), "Guard fired exactly once");
        Assert.AreEqual(4, provider.CallCount, "Loop continued past the premature Done");

        var refusalText = string.Join("\n", provider.Requests[2].Messages
            .OfType<UserMessage>().SelectMany(m => m.Content.OfType<TextBlock>()).Select(t => t.Text));
        StringAssert.Contains(refusalText, "build is FAILING");
    }

    [TestMethod]
    public async Task CompletionOverFailingBuild_Disabled_EndsImmediately()
    {
        // Guard off: Done is accepted even though the last build failed (legacy behaviour).
        var (loop, provider) = BuildGuardLoop(verify: false,
            ShellTurn("s1", "dotnet build # fail"),
            DoneTurn("d1"));

        var events = await Collect(loop.RunAsync(EmptyCtx(), Path.GetTempPath(), CancellationToken.None));

        var ended = events.OfType<LoopEnded>().Single();
        Assert.AreEqual(EndReason.TaskComplete, ended.Reason);
        Assert.AreEqual(0, events.OfType<AutoContinued>().Count());
        Assert.AreEqual(2, provider.CallCount);
    }

    [TestMethod]
    public async Task CompletionOverFailingBuild_ExhaustsGuard_ThenEnds()
    {
        // The model keeps signalling Done over a persistently failing build. The guard is bounded
        // by AutoContinueMaxAttempts (3), after which the loop accepts completion rather than
        // looping forever.
        var (loop, provider) = BuildGuardLoop(verify: true,
            ShellTurn("s1", "dotnet build # fail"),
            DoneTurn("d1"), DoneTurn("d2"), DoneTurn("d3"), DoneTurn("d4"), DoneTurn("d5"));

        var events = await Collect(loop.RunAsync(EmptyCtx(), Path.GetTempPath(), CancellationToken.None));

        var ended = events.OfType<LoopEnded>().Single();
        Assert.AreEqual(EndReason.TaskComplete, ended.Reason);
        Assert.AreEqual(3, events.OfType<AutoContinued>().Count(), "Guard fires at most max-attempts times");
    }

    // ── Read de-duplication (end to end) ──────────────────────────────────────

    [TestMethod]
    public async Task RepeatRead_IsDedupedInLoop_WhenContentLive()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"dedup_{System.Guid.NewGuid():N}.cs");
        await System.IO.File.WriteAllTextAsync(tmp, "public class Foo\n{\n}\n");
        try
        {
            var config = MakeConfig(maxTurns: 100, nudgeLimit: 1000);
            config.Agent.DedupeReads = true;
            config.Compaction.ToolPairSummarize = false; // keep the first read's content live in context

            var p = tmp.Replace("\\", "\\\\");
            // Two reads of the SAME file+range but with cosmetically different args, so the exact-
            // duplicate loop guard doesn't skip the second — this is the repeat-read dedup targets.
            var read1 = $$"""{"path":"{{p}}"}""";
            var read2 = $$"""{"path":"{{p}}","offset":0}""";
            var provider = new FakeProvider(
                [new ToolCallDelta("r1", "Read", read1), new StreamEnd(StopReason.ToolUse)],
                [new ToolCallDelta("r2", "Read", read2), new StreamEnd(StopReason.ToolUse)],
                TextTurn("done"));
            var registry = new ToolRegistry();
            registry.Register(new ReadTool());
            var loop = new AgentLoop(provider, registry, YoloStore(), config);
            var ctx = EmptyCtx();

            await Collect(loop.RunAsync(ctx, Path.GetTempPath(), CancellationToken.None));

            var toolResults = ctx.Messages.OfType<UserMessage>()
                .SelectMany(m => m.Content.OfType<ToolResultBlock>())
                .Select(t => t.Content).ToList();
            Assert.IsTrue(toolResults.Any(c => c.Contains("public class Foo")), "first read returns the file content");
            Assert.IsTrue(toolResults.Any(c => c.Contains("already read")), "second read is de-duped to a stub");
        }
        finally { System.IO.File.Delete(tmp); }
    }

    // ── Rolling-window cycle guard ────────────────────────────────────────────

    // A ReadOnly tool that always succeeds, like Read/Grep against real files.
    private sealed class AlwaysOkTool : Dotsy.Core.Tools.Interfaces.ITool
    {
        public string Name => "Look";
        public string Description => "always ok";
        public System.Text.Json.JsonElement InputSchema =>
            System.Text.Json.JsonSerializer.SerializeToElement(new { type = "object" });
        public ToolSafety Safety => ToolSafety.ReadOnly;
        public bool IsCompletionSignal => false;
        public Task<ToolResult> ExecuteAsync(System.Text.Json.JsonElement input, ToolContext ctx, CancellationToken ct)
            => Task.FromResult(ToolResult.Ok("ok"));
    }

    private sealed class SlowWriteTool : Dotsy.Core.Tools.Interfaces.ITool
    {
        public string Name => "SlowWrite";
        public string Description => "slow write-like tool";
        public System.Text.Json.JsonElement InputSchema =>
            System.Text.Json.JsonSerializer.SerializeToElement(new { type = "object" });
        public ToolSafety Safety => ToolSafety.Sequential;
        public bool IsCompletionSignal => false;
        public bool IsWriteTool => true;

        public async Task<ToolResult> ExecuteAsync(System.Text.Json.JsonElement input, ToolContext ctx, CancellationToken ct)
        {
            await Task.Delay(30, ct);
            return ToolResult.Ok("wrote slowly");
        }
    }

    [TestMethod]
    public async Task SessionLog_RecordsToolRunAndApprovalDurationsSeparately()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"dotsy_tool_timing_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var config = MakeConfig(maxTurns: 1, nudgeLimit: 10);
            var provider = new FakeProvider(
                [
                    new UsageUpdate(123, 45, 7, 9),
                    new ToolCallDelta("call-1", "SlowWrite", "{}"),
                    new StreamEnd(StopReason.ToolUse)
                ]);
            var registry = new ToolRegistry();
            registry.Register(new SlowWriteTool());
            var store = new SessionStore("timing-session", tmp);
            var loop = new AgentLoop(
                provider,
                registry,
                new PermissionStore(config.Permissions, tmp),
                config,
                sessionStore: store);
            loop.PermissionPrompter = async (_, _, ct) =>
            {
                await Task.Delay(80, ct);
                return PermissionDecision.AllowOnce;
            };

            await Collect(loop.RunAsync(EmptyCtx(), tmp, CancellationToken.None));

            var records = File.ReadAllLines(Path.Combine(tmp, "timing-session.jsonl"))
                .Select(line => JsonDocument.Parse(line).RootElement.Clone())
                .ToList();
            var assistantRecord = records.Single(r => r.GetProperty("type").GetString() == "assistant");
            var usage = assistantRecord.GetProperty("usage");
            Assert.AreEqual(123, usage.GetProperty("inputTokens").GetInt32());
            Assert.AreEqual(45, usage.GetProperty("outputTokens").GetInt32());
            Assert.AreEqual(7, usage.GetProperty("cacheReadTokens").GetInt32());
            Assert.AreEqual(9, usage.GetProperty("cacheWriteTokens").GetInt32());
            Assert.AreEqual(200_000, usage.GetProperty("contextWindowTokens").GetInt32());
            Assert.AreEqual(1_024, usage.GetProperty("maxOutputTokens").GetInt32());
            Assert.AreEqual(16_384, usage.GetProperty("reserveTokens").GetInt32());
            Assert.AreEqual(168, usage.GetProperty("usedTokens").GetInt32());

            var toolRecord = records.Single(r => r.GetProperty("type").GetString() == "tool_result");
            var toolResult = toolRecord.GetProperty("message").GetProperty("content")[0];

            var durationMs = toolResult.GetProperty("duration_ms").GetInt64();
            var approvalWaitMs = toolResult.GetProperty("approval_wait_ms").GetInt64();
            Assert.IsTrue(durationMs >= 20, $"Expected run duration to include tool work, got {durationMs}ms");
            Assert.IsTrue(durationMs < 80, $"Expected run duration to exclude approval wait, got {durationMs}ms");
            Assert.IsTrue(approvalWaitMs >= 60, $"Expected approval wait to be logged separately, got {approvalWaitMs}ms");
        }
        finally
        {
            DeleteDirectory(tmp);
        }
    }

    [TestMethod]
    public async Task RepeatingCycle_OfSuccessfulTools_BreaksLoop()
    {
        // A 3-step cycle (a,b,c,a,b,c…) of successful reads. No two adjacent turns are identical,
        // so the exact-duplicate trap never fires; only the rolling-window guard can stop this.
        var config = MakeConfig(maxTurns: 1000, nudgeLimit: 1000);
        config.Agent.RepeatWindowTurns = 8;
        config.Agent.RepeatThreshold = 3;

        var keys = new[] { "a", "b", "c" };
        var turns = new List<IReadOnlyList<ProviderEvent>>();
        for (int cycle = 0; cycle < 4; cycle++)
            foreach (var k in keys)
                turns.Add([new ToolCallDelta("c", "Look", $$"""{"p":"{{k}}"}"""), new StreamEnd(StopReason.ToolUse)]);

        var provider = new FakeProvider(turns.ToArray());
        var registry = new ToolRegistry();
        registry.Register(new AlwaysOkTool());
        var loop = new AgentLoop(provider, registry, YoloStore(), config);

        var events = await Collect(loop.RunAsync(EmptyCtx(), Path.GetTempPath(), CancellationToken.None));

        var ended = events.OfType<LoopEnded>().LastOrDefault();
        Assert.IsNotNull(ended, "Expected the loop to stop");
        Assert.AreEqual(EndReason.Repetition, ended.Reason);
        Assert.IsTrue(provider.CallCount <= 9, $"Loop should bail within ~2 cycles, made {provider.CallCount} calls");
    }

    [TestMethod]
    public async Task RepeatLoop_WithAutoContinue_EscalatesThenRecoversWhenModelActs()
    {
        // A weak model re-reads the same file, tripping the repetition guard. With auto-continue on,
        // the loop no longer bails after 2 strikes — it injects escalating hints (AutoContinued) and
        // recovers once the model finally does something different.
        var config = MakeConfig(maxTurns: 100, nudgeLimit: 1000, autoContinue: true, autoContinueMax: 3);
        var provider = new FakeProvider(
            [new ToolCallDelta("c", "Look", """{"p":"a"}"""), new StreamEnd(StopReason.ToolUse)], // execute
            [new ToolCallDelta("c", "Look", """{"p":"a"}"""), new StreamEnd(StopReason.ToolUse)], // dup -> guard 1
            [new ToolCallDelta("c", "Look", """{"p":"a"}"""), new StreamEnd(StopReason.ToolUse)], // dup -> guard 2
            [new ToolCallDelta("c", "Look", """{"p":"b"}"""), new StreamEnd(StopReason.ToolUse)], // different -> progress
            [new ToolCallDelta("done", "Done", """{"summary":"ok"}"""), new StreamEnd(StopReason.ToolUse)]);
        var registry = new ToolRegistry();
        registry.Register(new AlwaysOkTool());
        registry.Register(new DoneTool());
        var loop = new AgentLoop(provider, registry, YoloStore(), config);

        var events = await Collect(loop.RunAsync(EmptyCtx(), Path.GetTempPath(), CancellationToken.None));

        var ended = events.OfType<LoopEnded>().Single();
        Assert.AreEqual(EndReason.TaskComplete, ended.Reason);
        Assert.AreEqual(2, events.OfType<AutoContinued>().Count(),
            "Two escalating repetition-recovery hints before the model broke out");
        Assert.AreEqual(5, provider.CallCount);
    }

    [TestMethod]
    public async Task RepeatLoop_WithAutoContinue_StillEndsRepetition_WhenModelNeverActs()
    {
        // Bound check: if the model keeps repeating forever, the loop still terminates — just after a
        // few more (budgeted) chances rather than immediately.
        var config = MakeConfig(maxTurns: 100, nudgeLimit: 1000, autoContinue: true, autoContinueMax: 2);
        var provider = new FakeProvider(
            [new ToolCallDelta("c", "Look", """{"p":"a"}"""), new StreamEnd(StopReason.ToolUse)]);
        var registry = new ToolRegistry();
        registry.Register(new AlwaysOkTool());
        var loop = new AgentLoop(provider, registry, YoloStore(), config);

        var events = await Collect(loop.RunAsync(EmptyCtx(), Path.GetTempPath(), CancellationToken.None));

        Assert.AreEqual(EndReason.Repetition, events.OfType<LoopEnded>().Single().Reason);
        // repeatBailAt = 1 + max(1, 2) = 3: hints fire on the 1st and 2nd repeat, the 3rd repeat ends.
        Assert.AreEqual(2, events.OfType<AutoContinued>().Count());
        Assert.AreEqual(4, provider.CallCount, "initial execute + 3 repeating turns (the 3rd ends)");
    }

    [TestMethod]
    public async Task DistinctReads_DoNotTripCycleGuard()
    {
        // Every turn reads a different file (genuine progress); the guard must not fire. The run
        // ends via TaskComplete on the final Done call, proving all distinct turns executed.
        var config = MakeConfig(maxTurns: 1000, nudgeLimit: 1000);
        config.Agent.RepeatWindowTurns = 8;
        config.Agent.RepeatThreshold = 3;

        var turns = new List<IReadOnlyList<ProviderEvent>>();
        for (int i = 0; i < 6; i++)
            turns.Add([new ToolCallDelta("c", "Look", $$"""{"p":"file{{i}}"}"""), new StreamEnd(StopReason.ToolUse)]);
        turns.Add([new ToolCallDelta("done", "Done", "{\"summary\":\"finished\"}"), new StreamEnd(StopReason.ToolUse)]);

        var provider = new FakeProvider(turns.ToArray());
        var registry = new ToolRegistry();
        registry.Register(new AlwaysOkTool());
        registry.Register(new DoneTool());
        var loop = new AgentLoop(provider, registry, YoloStore(), config);

        var events = await Collect(loop.RunAsync(EmptyCtx(), Path.GetTempPath(), CancellationToken.None));

        var ended = events.OfType<LoopEnded>().LastOrDefault();
        Assert.IsNotNull(ended);
        Assert.AreEqual(EndReason.TaskComplete, ended.Reason);
    }

    // ── DoneTool completion signal ────────────────────────────────────────────

    [TestMethod]
    public async Task DoneTool_BreaksLoopWithTaskComplete()
    {
        var config   = MakeConfig(maxTurns: 100, nudgeLimit: 100);
        var response = new IReadOnlyList<ProviderEvent>[]
        {
            [new ToolCallDelta("call-1", "Done", "{\"summary\":\"all done\"}"),
             new StreamEnd(StopReason.ToolUse)]
        };
        var provider = new FakeProvider(response);
        var registry = new ToolRegistry();
        registry.Register(new DoneTool());
        var loop = new AgentLoop(provider, registry, YoloStore(), config);

        var events = await Collect(loop.RunAsync(EmptyCtx(), Path.GetTempPath(), CancellationToken.None));

        var ended = events.OfType<LoopEnded>().LastOrDefault();
        Assert.IsNotNull(ended);
        Assert.AreEqual(EndReason.TaskComplete, ended.Reason);
        Assert.AreEqual(1, provider.CallCount, "Only one API call needed");
    }

    [TestMethod]
    public async Task SubTaskManager_LaunchesAgentAndReportsResult()
    {
        var config = MakeConfig(maxTurns: 10, nudgeLimit: 1);
        var registry = new ToolRegistry();
        var manager = new AgentSubTaskManager(
            () => new FakeProvider(TextTurn("sub result")),
            registry,
            YoloStore(),
            config,
            Path.GetTempPath());

        var taskId = await manager.LaunchAsync("scan", "find docs", CancellationToken.None);
        string status = "";
        for (var i = 0; i < 50; i++)
        {
            status = await manager.GetStatusAsync(taskId, CancellationToken.None);
            if (status.Contains("status=completed", StringComparison.Ordinal))
                break;
            await Task.Delay(20);
        }

        StringAssert.Contains(status, $"task_id={taskId}");
        StringAssert.Contains(status, "status=completed");
        StringAssert.Contains(status, "description=scan");
        StringAssert.Contains(status, "sub result");
    }

    [TestMethod]
    public async Task RepoMap_UsesCurrentUserMessageForPersonalization()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"dotsy_repo_map_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tmp, "Alpha.cs"), "public class Alpha { public void Run() {} }\n");
            await File.WriteAllTextAsync(Path.Combine(tmp, "Beta.cs"), "public class Beta { public void Run() {} }\n");

            var config = MakeConfig(maxTurns: 1, nudgeLimit: 10);
            config.Retrieval.RepoMapTokens = 200;
            var provider = new FakeProvider(TextTurn());
            var loop = new AgentLoop(provider, new ToolRegistry(), YoloStore(), config);
            var ctx = EmptyCtx();
            ctx.Messages.Add(new UserMessage([new TextBlock("Please inspect Beta.cs")]));

            await Collect(loop.RunAsync(ctx, tmp, CancellationToken.None));

            StringAssert.Contains(provider.Requests[0].SystemPrompt, "<repo_map>");
            StringAssert.Contains(provider.Requests[0].SystemPrompt, "Beta.cs");
        }
        finally
        {
            if (Directory.Exists(tmp))
                Directory.Delete(tmp, recursive: true);
        }
    }

    [TestMethod]
    public async Task RepoMap_UsesAddedFilesForPersonalization()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"dotsy_repo_map_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var alpha = Path.Combine(tmp, "Alpha.cs");
            await File.WriteAllTextAsync(alpha, "public class Alpha { public void Run() {} }\n");
            await File.WriteAllTextAsync(Path.Combine(tmp, "Beta.cs"), "public class Beta { public void Run() {} }\n");

            var config = MakeConfig(maxTurns: 1, nudgeLimit: 10);
            config.Retrieval.RepoMapTokens = 200;
            var provider = new FakeProvider(TextTurn());
            var loop = new AgentLoop(provider, new ToolRegistry(), YoloStore(), config);
            var ctx = EmptyCtx();
            ctx.AddedFiles.Add(alpha);
            ctx.Messages.Add(new UserMessage([new TextBlock("What should change next?")]));

            await Collect(loop.RunAsync(ctx, tmp, CancellationToken.None));

            StringAssert.Contains(provider.Requests[0].SystemPrompt, "<repo_map>");
            StringAssert.Contains(provider.Requests[0].SystemPrompt, "Alpha.cs");
        }
        finally
        {
            if (Directory.Exists(tmp))
                Directory.Delete(tmp, recursive: true);
        }
    }

    [TestMethod]
    public async Task LivePrompt_IncludesAvailableSkillsAndAddedFiles()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"dotsy_live_prompt_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var skillDir = Path.Combine(tmp, ".dotsy", "skills");
            Directory.CreateDirectory(skillDir);
            await File.WriteAllTextAsync(Path.Combine(skillDir, "helper.md"), "# Helper\nUse helper skill.\n");
            await File.WriteAllTextAsync(Path.Combine(tmp, "context.txt"), "added file context\n");

            var config = MakeConfig(maxTurns: 1, nudgeLimit: 10);
            var provider = new FakeProvider(TextTurn());
            var loop = new AgentLoop(provider, new ToolRegistry(), YoloStore(), config);
            var ctx = EmptyCtx();
            ctx.AddedFiles.Add("context.txt");
            ctx.Messages.Add(new UserMessage([new TextBlock("use context")]));

            await Collect(loop.RunAsync(ctx, tmp, CancellationToken.None));

            var prompt = provider.Requests[0].SystemPrompt;
            StringAssert.Contains(prompt, "<available_skills>");
            StringAssert.Contains(prompt, "helper: Use helper skill.");
            StringAssert.Contains(prompt, "<added_files>");
            StringAssert.Contains(prompt, "<file path=\"context.txt\">");
            StringAssert.Contains(prompt, "added file context");
        }
        finally
        {
            DeleteDirectory(tmp);
        }
    }

    [TestMethod]
    public async Task GitContext_IsIncludedInLiveSystemPrompt()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"dotsy_git_prompt_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            if (!await GitSucceeds(tmp, "init"))
                Assert.Inconclusive("git is not available");

            await File.WriteAllTextAsync(Path.Combine(tmp, "tracked.txt"), "old\n");
            await GitSucceeds(tmp, "config", "user.email", "test@example.com");
            await GitSucceeds(tmp, "config", "user.name", "Dotsy Test");
            await GitSucceeds(tmp, "add", "tracked.txt");
            await GitSucceeds(tmp, "commit", "-m", "initial");
            await File.WriteAllTextAsync(Path.Combine(tmp, "tracked.txt"), "new\n");

            var config = MakeConfig(maxTurns: 1, nudgeLimit: 10);
            config.Agent.InjectEnvironment = true;
            config.Agent.InjectGitStatus = true;
            var provider = new FakeProvider(TextTurn());
            var loop = new AgentLoop(provider, new ToolRegistry(), YoloStore(), config);
            var ctx = EmptyCtx();
            ctx.Messages.Add(new UserMessage([new TextBlock("status?")]));

            await Collect(loop.RunAsync(ctx, tmp, CancellationToken.None));

            StringAssert.Contains(provider.Requests[0].SystemPrompt, "git_branch:");
            StringAssert.Contains(provider.Requests[0].SystemPrompt, "git_status:");
            StringAssert.Contains(provider.Requests[0].SystemPrompt, "1 modified");
        }
        finally
        {
            DeleteDirectory(tmp);
        }
    }

    [TestMethod]
    public async Task AutoCommit_UsesConfiguredGitIdentity()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"dotsy_auto_commit_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            if (!await GitSucceeds(tmp, "init"))
                Assert.Inconclusive("git is not available");

            await GitSucceeds(tmp, "config", "user.email", "user@example.com");
            await GitSucceeds(tmp, "config", "user.name", "Configured User");
            await File.WriteAllTextAsync(Path.Combine(tmp, "README.md"), "initial\n");
            await GitSucceeds(tmp, "add", "README.md");
            await GitSucceeds(tmp, "commit", "-m", "initial");

            var config = MakeConfig(maxTurns: 2, nudgeLimit: 10);
            config.Agent.AutoCommit = true;
            var provider = new FakeProvider(
                [new ToolCallDelta("write-1", "Write", """{"path":"agent.txt","content":"new\n"}"""), new StreamEnd(StopReason.ToolUse)],
                [new ToolCallDelta("done-1", "Done", """{"summary":"done"}"""), new StreamEnd(StopReason.ToolUse)]);
            var registry = new ToolRegistry();
            registry.Register(new WriteTool());
            registry.Register(new DoneTool());
            var ctx = EmptyCtx();
            var loop = new AgentLoop(provider, registry, YoloStore(), config);

            await Collect(loop.RunAsync(ctx, tmp, CancellationToken.None));

            var author = await GitOutput(tmp, "log", "-1", "--format=%an <%ae>");
            Assert.AreEqual("Configured User <user@example.com>", author.Trim());
        }
        finally
        {
            DeleteDirectory(tmp);
        }
    }

    [TestMethod]
    public async Task AutoCommit_LeavesDirtyStartChangesUncommitted()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"dotsy_dirty_start_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            if (!await GitSucceeds(tmp, "init"))
                Assert.Inconclusive("git is not available");

            await GitSucceeds(tmp, "config", "user.email", "test@example.com");
            await GitSucceeds(tmp, "config", "user.name", "Dotsy Test");
            await File.WriteAllTextAsync(Path.Combine(tmp, "dirty.txt"), "clean\n");
            await GitSucceeds(tmp, "add", "dirty.txt");
            await GitSucceeds(tmp, "commit", "-m", "initial");
            await File.WriteAllTextAsync(Path.Combine(tmp, "dirty.txt"), "dirty\n");

            var config = MakeConfig(maxTurns: 2, nudgeLimit: 10);
            config.Agent.AutoCommit = true;
            var provider = new FakeProvider(
                [new ToolCallDelta("write-1", "Write", """{"path":"agent.txt","content":"new\n"}"""), new StreamEnd(StopReason.ToolUse)],
                [new ToolCallDelta("done-1", "Done", """{"summary":"done"}"""), new StreamEnd(StopReason.ToolUse)]);
            var registry = new ToolRegistry();
            registry.Register(new WriteTool());
            registry.Register(new DoneTool());
            var loop = new AgentLoop(provider, registry, YoloStore(), config);

            await Collect(loop.RunAsync(EmptyCtx(), tmp, CancellationToken.None));

            var committedFiles = await GitOutput(tmp, "show", "--name-only", "--format=", "HEAD");
            StringAssert.Contains(committedFiles, "agent.txt");
            Assert.IsFalse(committedFiles.Contains("dirty.txt", StringComparison.Ordinal));

            var status = await GitOutput(tmp, "status", "--short");
            StringAssert.Contains(status, " M dirty.txt");
        }
        finally
        {
            DeleteDirectory(tmp);
        }
    }

    [TestMethod]
    public async Task AutoCommit_DoesNotCommitCleanNoOpTurn()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"dotsy_noop_commit_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            if (!await GitSucceeds(tmp, "init"))
                Assert.Inconclusive("git is not available");

            await GitSucceeds(tmp, "config", "user.email", "test@example.com");
            await GitSucceeds(tmp, "config", "user.name", "Dotsy Test");
            await File.WriteAllTextAsync(Path.Combine(tmp, "README.md"), "initial\n");
            await GitSucceeds(tmp, "add", "README.md");
            await GitSucceeds(tmp, "commit", "-m", "initial");

            var before = (await GitOutput(tmp, "rev-list", "--count", "HEAD")).Trim();
            var config = MakeConfig(maxTurns: 1, nudgeLimit: 10);
            config.Agent.AutoCommit = true;
            var loop = new AgentLoop(new FakeProvider(TextTurn()), new ToolRegistry(), YoloStore(), config);
            var ctx = EmptyCtx();
            ctx.Messages.Add(new UserMessage([new TextBlock("answer only")]));

            await Collect(loop.RunAsync(ctx, tmp, CancellationToken.None));

            var after = (await GitOutput(tmp, "rev-list", "--count", "HEAD")).Trim();
            Assert.AreEqual(before, after);
        }
        finally
        {
            DeleteDirectory(tmp);
        }
    }

    [TestMethod]
    public async Task CompactionRunsBeforeContextTooSmall_AndCarriesSummaryForward()
    {
        var config = MakeCompactionConfig();
        var provider = new FakeProvider(
            [new TextDelta("summary of old work"), new StreamEnd(StopReason.EndTurn)],
            TextTurn("continued"));
        var registry = new ToolRegistry();
        var loop = new AgentLoop(provider, registry, YoloStore(), config);
        var ctx = new LoopContext
        {
            TokenBudget = new TokenBudget(1_000, 100, 1, 950, 0.9f)
        };

        ctx.Messages.Add(new UserMessage([new TextBlock("old user")]));
        ctx.Messages.Add(new AssistantMessage([new TextBlock("old assistant")]));
        ctx.Messages.Add(new UserMessage([new TextBlock("older detail")]));
        ctx.Messages.Add(new AssistantMessage([new TextBlock("recent answer")]));
        ctx.Messages.Add(new UserMessage([new TextBlock("latest user")]));

        var events = await Collect(loop.RunAsync(ctx, Path.GetTempPath(), CancellationToken.None));

        var compacted = events.OfType<CompactionOccurred>().SingleOrDefault();
        Assert.IsNotNull(compacted);
        Assert.AreEqual("summary of old work", ctx.CompactionSummary);
        Assert.AreEqual(3, ctx.Messages.Count, "Recent messages plus the new assistant turn should remain");
        Assert.IsTrue(provider.Requests[0].Messages[0] is UserMessage, "First request should be the summary request");
        Assert.IsTrue(provider.Requests[1].SystemPrompt.Contains("<prior_context>"));
        Assert.IsTrue(provider.Requests[1].SystemPrompt.Contains("summary of old work"));
        Assert.IsTrue(provider.Requests[1].SystemPrompt.Contains(SystemPromptBuilder.CompactionContinuationInstruction));
        Assert.IsFalse(ctx.Messages
            .SelectMany(m => m switch
            {
                UserMessage u => u.Content,
                AssistantMessage a => a.Content,
                _ => []
            })
            .OfType<TextBlock>()
            .Any(tb => tb.Text.Contains(SystemPromptBuilder.CompactionContinuationInstruction, StringComparison.Ordinal)));

        var ended = events.OfType<LoopEnded>().LastOrDefault();
        Assert.IsNotNull(ended);
        Assert.AreNotEqual(EndReason.ContextTooSmall, ended.Reason);
    }

    [TestMethod]
    public async Task ManualCompact_EmitsCompactionOccurredAndCarriesSummaryForward()
    {
        var config = MakeCompactionConfig();
        var provider = new FakeProvider(
            [new TextDelta("manual summary"), new StreamEnd(StopReason.EndTurn)]);
        var loop = new AgentLoop(provider, new ToolRegistry(), YoloStore(), config);
        var ctx = new LoopContext
        {
            TokenBudget = new TokenBudget(1_000, 100, 1, 200, 0.9f)
        };
        ctx.Messages.Add(new UserMessage([new TextBlock("old user")]));
        ctx.Messages.Add(new AssistantMessage([new TextBlock("old assistant")]));
        ctx.Messages.Add(new UserMessage([new TextBlock("latest user")]));

        var events = await Collect(loop.CompactAsync(ctx, Path.GetTempPath(), CancellationToken.None));

        var compacted = events.OfType<CompactionOccurred>().SingleOrDefault();
        Assert.IsNotNull(compacted);
        Assert.AreEqual("manual summary", ctx.CompactionSummary);
        Assert.AreEqual(2, ctx.Messages.Count, "Manual compaction should keep the recent tail verbatim");
    }

    [TestMethod]
    public async Task ContextLengthError_CompactsAndRetriesOnce()
    {
        var config = MakeCompactionConfig();
        var provider = new FakeProvider(
            [new StreamError(new ProviderException(new ContextLengthError()))],
            [new TextDelta("summary after context error"), new StreamEnd(StopReason.EndTurn)],
            TextTurn("continued"));
        var loop = new AgentLoop(provider, new ToolRegistry(), YoloStore(), config);
        var ctx = new LoopContext
        {
            TokenBudget = new TokenBudget(1_000, 100, 1, 100, 0.9f)
        };
        ctx.Messages.Add(new UserMessage([new TextBlock("old user")]));
        ctx.Messages.Add(new AssistantMessage([new TextBlock("recent assistant")]));
        ctx.Messages.Add(new UserMessage([new TextBlock("latest user")]));

        var events = await Collect(loop.RunAsync(ctx, Path.GetTempPath(), CancellationToken.None));

        Assert.AreEqual(3, provider.CallCount, "Expected initial request, summary request, then retry");
        Assert.IsNotNull(events.OfType<CompactionOccurred>().SingleOrDefault());
        Assert.AreEqual("summary after context error", ctx.CompactionSummary);
        Assert.IsTrue(events.OfType<TextChunk>().Any(t => t.Text == "continued"));
    }

    [TestMethod]
    public async Task ContextLengthError_SurfacesOnSecondFailure()
    {
        var config = MakeCompactionConfig();
        var provider = new FakeProvider(
            [new StreamError(new ProviderException(new ContextLengthError()))],
            [new TextDelta("summary after context error"), new StreamEnd(StopReason.EndTurn)],
            [new StreamError(new ProviderException(new ContextLengthError()))]);
        var loop = new AgentLoop(provider, new ToolRegistry(), YoloStore(), config);
        var ctx = new LoopContext
        {
            TokenBudget = new TokenBudget(1_000, 100, 1, 100, 0.9f)
        };
        ctx.Messages.Add(new UserMessage([new TextBlock("old user")]));
        ctx.Messages.Add(new AssistantMessage([new TextBlock("recent assistant")]));
        ctx.Messages.Add(new UserMessage([new TextBlock("latest user")]));

        var events = await Collect(loop.RunAsync(ctx, Path.GetTempPath(), CancellationToken.None));

        var ended = events.OfType<LoopEnded>().LastOrDefault();
        Assert.IsNotNull(ended);
        Assert.AreEqual(EndReason.ContextTooSmall, ended.Reason);
        StringAssert.Contains(ended.Message, "context window exceeded");
        Assert.AreEqual(3, provider.CallCount);
    }

    [TestMethod]
    public async Task WriteTurn_CreatesCheckpointThatUndoCanRestore()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"dotsy_loop_git_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            if (!await GitSucceeds(tmp, "init"))
                Assert.Inconclusive("git is not available");

            var file = Path.Combine(tmp, "tracked.txt");
            await File.WriteAllTextAsync(file, "old\n");
            await GitSucceeds(tmp, "config", "user.email", "test@example.com");
            await GitSucceeds(tmp, "config", "user.name", "Dotsy Test");
            await GitSucceeds(tmp, "add", "tracked.txt");
            await GitSucceeds(tmp, "commit", "-m", "initial");

            var config = MakeConfig(maxTurns: 10, nudgeLimit: 10);
            var provider = new FakeProvider(
                [new ToolCallDelta("write-1", "Write", """{"path":"tracked.txt","content":"new\n"}"""), new StreamEnd(StopReason.ToolUse)],
                [new ToolCallDelta("done-1", "Done", """{"summary":"done"}"""), new StreamEnd(StopReason.ToolUse)]);
            var registry = new ToolRegistry();
            registry.Register(new WriteTool());
            registry.Register(new DoneTool());
            var ctx = EmptyCtx();
            var loop = new AgentLoop(provider, registry, YoloStore(), config);

            await Collect(loop.RunAsync(ctx, tmp, CancellationToken.None));

            Assert.AreEqual("new\n", await File.ReadAllTextAsync(file));
            Assert.IsTrue(new GitIntegration(tmp).Undo(ctx.SessionId, ctx.TurnCount));
            Assert.AreEqual("old\n", (await File.ReadAllTextAsync(file)).Replace("\r\n", "\n"));
        }
        finally
        {
            if (Directory.Exists(tmp))
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(tmp, "*", SearchOption.AllDirectories))
                    File.SetAttributes(path, FileAttributes.Normal);
                Directory.Delete(tmp, recursive: true);
            }
        }
    }

    private static async Task<bool> GitSucceeds(string cwd, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            using var proc = Process.Start(psi);
            if (proc is null)
                return false;

            await proc.WaitForExitAsync();
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> GitOutput(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi);
        Assert.IsNotNull(proc);
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        Assert.AreEqual(0, proc.ExitCode, stderr);
        return stdout;
    }

    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
            return;

        foreach (var item in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(item, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }
}
