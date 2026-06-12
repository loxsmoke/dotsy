using System.Diagnostics;
using Dotsy.Core.Config;
using Dotsy.Core.Loop;
using Dotsy.Core.Providers;
using Dotsy.Core.Tests.Helpers;
using Dotsy.Core.Tools;

namespace Dotsy.Core.Tests;

[TestClass]
public sealed class AgentLoopTests
{
    private static DotsyConfig MakeConfig(int maxTurns = 1000, int nudgeLimit = 3) =>
        new()
        {
            Model    = new ModelConfig { Id = "test-model", MaxOutputTokensPerRequest = 1024 },
            Agent    = new AgentConfig
            {
                MaxTurns = maxTurns, NudgeLimit = nudgeLimit, ParallelTools = true,
                AutoLint = false, AutoTest = false, MaxReflections = 0,
                InjectEnvironment = false, InjectGitStatus = false
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
            Model = new ModelConfig { Id = "test-model", MaxOutputTokensPerRequest = 1024 },
            Agent = new AgentConfig
            {
                MaxTurns = 1, NudgeLimit = 1, ParallelTools = true,
                AutoLint = false, AutoTest = false, MaxReflections = 0,
                InjectEnvironment = false, InjectGitStatus = false
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
        TokenBudget = new TokenBudget(200_000, 16_384, 20_000, 0)
    };

    private static IReadOnlyList<ProviderEvent> TextTurn(string text = "hello") =>
        [new TextDelta(text), new StreamEnd(StopReason.EndTurn)];

    private static async Task<List<LoopEvent>> Collect(IAsyncEnumerable<LoopEvent> src)
    {
        var list = new List<LoopEvent>();
        await foreach (var ev in src)
            list.Add(ev);
        return list;
    }

    // ── Nudge limit ───────────────────────────────────────────────────────────

    [TestMethod]
    public async Task NudgeTriggers_AfterConsecutiveNoToolTurns()
    {
        var config   = MakeConfig(maxTurns: 100, nudgeLimit: 2);
        var provider = new FakeProvider(TextTurn());          // same sequence reused
        var registry = new ToolRegistry();
        var loop     = new AgentLoop(provider, registry, YoloStore(), config);

        var events = await Collect(loop.RunAsync(EmptyCtx(), Path.GetTempPath(), CancellationToken.None));

        var ended = events.OfType<LoopEnded>().LastOrDefault();
        Assert.IsNotNull(ended, "Expected a LoopEnded event");
        Assert.AreEqual(EndReason.NudgeLimitReached, ended.Reason);
        Assert.AreEqual(2, provider.CallCount, "Provider called once per nudge turn");
    }

    // ── MaxTurns ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task MaxTurns_YieldsLoopEndedTurnLimitReached()
    {
        var config   = MakeConfig(maxTurns: 2, nudgeLimit: 100);
        var provider = new FakeProvider(TextTurn());
        var registry = new ToolRegistry();
        var loop     = new AgentLoop(provider, registry, YoloStore(), config);

        var events = await Collect(loop.RunAsync(EmptyCtx(), Path.GetTempPath(), CancellationToken.None));

        var ended = events.OfType<LoopEnded>().LastOrDefault();
        Assert.IsNotNull(ended);
        Assert.AreEqual(EndReason.TurnLimitReached, ended.Reason);
        Assert.AreEqual(2, events.OfType<TurnComplete>().Count(), "Exactly 2 turns ran");
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
            TokenBudget = new TokenBudget(1_000, 100, 1, 950)
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
            TokenBudget = new TokenBudget(1_000, 100, 1, 200)
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
            TokenBudget = new TokenBudget(1_000, 100, 1, 100)
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
            TokenBudget = new TokenBudget(1_000, 100, 1, 100)
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
