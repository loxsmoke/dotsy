using Dotsy.Core.Config;
using Dotsy.Core.Loop;
using Dotsy.Core.Loop.Data;
using Dotsy.Core.Providers;
using Dotsy.Core.Tests.Helpers;
using Dotsy.Core.Tools;

namespace Dotsy.Core.Tests;

[TestClass]
public sealed class ToolApprovalTests
{
    private string _tmpDir = "";

    [TestInitialize]
    public void Setup()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"dotsy_approval_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
    }

    [TestCleanup]
    public void Teardown()
    {
        if (Directory.Exists(_tmpDir))
            Directory.Delete(_tmpDir, recursive: true);
    }

    private static DotsyConfig MakeConfig() => new()
    {
        Model      = new ModelConfig { Anthropic = new() { Id = "test-model" }, MaxOutputTokensPerRequest = 1024 },
        Agent      = new AgentConfig
        {
            MaxTurns = 100, NudgeLimit = 3, ParallelTools = false,
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

    private PermissionStore AskStore() =>
        new(new PermissionsConfig { AlwaysAllow = [], NeverAllow = [] }, _tmpDir);

    private static LoopContext Ctx() => new()
    {
        TokenBudget = new TokenBudget(200_000, 16_384, 20_000, 0)
    };

    private static async Task<List<LoopEvent>> Collect(IAsyncEnumerable<LoopEvent> src)
    {
        var list = new List<LoopEvent>();
        await foreach (var ev in src) list.Add(ev);
        return list;
    }

    // Two-turn provider: first calls the tool under test, second signals done.
    private static FakeProvider MakeProvider(string toolName, string argsJson) =>
        new(
            [new ToolCallDelta("call-1", toolName, argsJson), new StreamEnd(StopReason.ToolUse)],
            [new ToolCallDelta("call-done", "Done", "{\"summary\":\"done\"}"), new StreamEnd(StopReason.ToolUse)]);

    private static ToolRegistry MakeRegistry()
    {
        var r = new ToolRegistry();
        r.Register(new DoneTool());
        r.Register(new EditTool());
        r.Register(new MultiEditTool());
        r.Register(new ShellTool());
        r.Register(new WebFetchTool());
        r.Register(new WebSearchTool());
        r.Register(new WriteTool());
        return r;
    }

    private string ArgsFor(string toolName) => toolName switch
    {
        EditTool.ToolName      => $"{{\"path\":\"{Esc(Path.Combine(_tmpDir, "f.cs"))}\",\"start_line\":1,\"end_line\":1,\"new_string\":\"y\"}}",
        MultiEditTool.ToolName => $"{{\"path\":\"{Esc(Path.Combine(_tmpDir, "f.cs"))}\",\"edits\":[{{\"start_line\":1,\"end_line\":1,\"new_string\":\"y\"}}]}}",
        ShellTool.ToolName     => "{\"command\":\"echo hi\"}",
        WebFetchTool.ToolName  => "{\"url\":\"https://example.com\"}",
        WebSearchTool.ToolName => "{\"query\":\"hello\"}",
        WriteTool.ToolName     => $"{{\"path\":\"{Esc(Path.Combine(_tmpDir, "out.txt"))}\",\"content\":\"test\"}}",
        _ => throw new ArgumentException(toolName)
    };

    private static string Esc(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    // ── No prompter blocks all tools that require confirmation ────────────────

    [DataTestMethod]
    [DataRow(EditTool.ToolName)]
    [DataRow(MultiEditTool.ToolName)]
    [DataRow(ShellTool.ToolName)]
    [DataRow(WebFetchTool.ToolName)]
    [DataRow(WebSearchTool.ToolName)]
    [DataRow(WriteTool.ToolName)]
    public async Task NoPrompter_ReturnsPermissionError(string toolName)
    {
        var loop = new AgentLoop(MakeProvider(toolName, ArgsFor(toolName)), MakeRegistry(), AskStore(), MakeConfig());
        // No PermissionPrompter assigned

        var events = await Collect(loop.RunAsync(Ctx(), _tmpDir, CancellationToken.None));

        var finished = events.OfType<ToolFinished>().FirstOrDefault(tf =>
            string.Equals(tf.Name, toolName, StringComparison.OrdinalIgnoreCase));
        Assert.IsNotNull(finished, $"No ToolFinished event for {toolName}");
        Assert.IsTrue(finished.Result.IsError, $"{toolName}: expected error result");
        StringAssert.Contains(finished.Result.Content, "Permission required",
            $"{toolName}: expected 'Permission required' in error message");
    }

    // ── Deny decision blocks execution ────────────────────────────────────────

    [DataTestMethod]
    [DataRow(EditTool.ToolName)]
    [DataRow(MultiEditTool.ToolName)]
    [DataRow(ShellTool.ToolName)]
    [DataRow(WebFetchTool.ToolName)]
    [DataRow(WebSearchTool.ToolName)]
    [DataRow(WriteTool.ToolName)]
    public async Task DenyDecision_ReturnsPermissionDeniedError(string toolName)
    {
        var loop = new AgentLoop(MakeProvider(toolName, ArgsFor(toolName)), MakeRegistry(), AskStore(), MakeConfig());
        loop.PermissionPrompter = (_, _, _) => Task.FromResult(PermissionDecision.Deny);

        var events = await Collect(loop.RunAsync(Ctx(), _tmpDir, CancellationToken.None));

        var finished = events.OfType<ToolFinished>().FirstOrDefault(tf =>
            string.Equals(tf.Name, toolName, StringComparison.OrdinalIgnoreCase));
        Assert.IsNotNull(finished, $"No ToolFinished event for {toolName}");
        Assert.IsTrue(finished.Result.IsError, $"{toolName}: expected error result");
        StringAssert.Contains(finished.Result.Content, "Permission denied",
            $"{toolName}: expected 'Permission denied' in error message");
    }

    // ── AllowOnce permits actual execution ────────────────────────────────────

    [TestMethod]
    public async Task AllowOnce_Write_CreatesFile()
    {
        var outFile = Path.Combine(_tmpDir, "out.txt");
        var argsJson = $"{{\"path\":\"{Esc(outFile)}\",\"content\":\"hello\"}}";

        var loop = new AgentLoop(MakeProvider("Write", argsJson), MakeRegistry(), AskStore(), MakeConfig());
        loop.PermissionPrompter = (_, _, _) => Task.FromResult(PermissionDecision.AllowOnce);

        var events = await Collect(loop.RunAsync(Ctx(), _tmpDir, CancellationToken.None));

        var finished = events.OfType<ToolFinished>().FirstOrDefault(tf => tf.Name == "Write");
        Assert.IsNotNull(finished, "No ToolFinished event for Write");
        Assert.IsFalse(finished.Result.IsError, finished.Result.Content);
        Assert.IsTrue(File.Exists(outFile), "Write tool should have created the file");
        Assert.AreEqual("hello", await File.ReadAllTextAsync(outFile));
    }

    [TestMethod]
    public async Task AllowOnce_Shell_ReturnsOutput()
    {
        var loop = new AgentLoop(
            MakeProvider("Shell", "{\"command\":\"echo dotsy-test\"}"),
            MakeRegistry(), AskStore(), MakeConfig());
        loop.PermissionPrompter = (_, _, _) => Task.FromResult(PermissionDecision.AllowOnce);

        var events = await Collect(loop.RunAsync(Ctx(), _tmpDir, CancellationToken.None));

        var finished = events.OfType<ToolFinished>().FirstOrDefault(tf => tf.Name == "Shell");
        Assert.IsNotNull(finished, "No ToolFinished event for Shell");
        Assert.IsFalse(finished.Result.IsError, finished.Result.Content);
        StringAssert.Contains(finished.Result.Content, "dotsy-test");
    }
}
