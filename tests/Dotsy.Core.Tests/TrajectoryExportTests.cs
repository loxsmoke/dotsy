using System.Text.Json;
using Dotsy.Core.Config;
using Dotsy.Core.Loop;
using Dotsy.Core.Providers;
using Dotsy.Core.Session;
using Dotsy.Core.Tests.Helpers;
using Dotsy.Core.Tools;

namespace Dotsy.Core.Tests;

[TestClass]
public sealed class TrajectoryExportTests
{
    private static DotsyConfig MakeIntegrationConfig(string tmp, bool trajectoryEnabled) =>
        new()
        {
            Model = new ModelConfig { Provider = "fake", Id = "test-model", MaxOutputTokensPerRequest = 1024 },
            Agent = new AgentConfig
            {
                MaxTurns = 1,
                NudgeLimit = 10,
                ParallelTools = true,
                InjectEnvironment = false,
                InjectGitStatus = false
            },
            Compaction = new CompactionConfig { Enabled = false },
            Retrieval = new RetrievalConfig { RepoMapTokens = 0 },
            Skills = new SkillsConfig { Paths = [] },
            Permissions = new PermissionsConfig { AlwaysAllow = [], NeverAllow = [] },
            Session = new SessionConfig
            {
                LogEnabled = true,
                LogDir = Path.Combine(tmp, "sessions")
            },
            Trajectory = new TrajectoryConfig
            {
                Enabled = trajectoryEnabled,
                Dir = Path.Combine(tmp, "trajectories")
            }
        };

    private static PermissionStore YoloStore(string cwd) =>
        new(new PermissionsConfig { AlwaysAllow = [], NeverAllow = [] }, cwd) { Yolo = true };

    private static async Task<LoopEnded> RunLoopAndExport(
        DotsyConfig config,
        string tmp,
        bool noHistory = false)
    {
        var provider = new FakeProvider([
            new TextDelta("done"),
            new UsageUpdate(11, 7, 3, 2),
            new StreamEnd(StopReason.EndTurn)
        ]);
        var registry = new ToolRegistry();
        registry.Register(new ReadTool());
        var sessionId = "integration-session";
        var ctx = new LoopContext(sessionId)
        {
            TokenBudget = new TokenBudget(200_000, 16_384, 20_000, 0)
        };
        ctx.Messages.Add(new UserMessage([new TextBlock("hello integration")]));
        var sessionStore = new SessionStore(
            sessionId,
            SessionStore.ResolveDir(config.Session.LogDir, tmp),
            disabled: noHistory || !config.Session.LogEnabled);
        sessionStore.Append(new SessionRecord
        {
            Type = "user",
            Cwd = tmp,
            Message = new { content = "hello integration" }
        });
        var trajectory = new TrajectoryRecorder(config, tmp);
        var loop = new AgentLoop(
            provider,
            registry,
            YoloStore(tmp),
            config,
            sessionStore: sessionStore,
            trajectory: trajectory);

        LoopEnded? ended = null;
        await foreach (var ev in loop.RunAsync(ctx, tmp, CancellationToken.None))
        {
            if (ev is LoopEnded le)
                ended = le;
        }

        Assert.IsNotNull(ended);
        trajectory.Export(ctx, ended.Reason, ended.Message);
        return ended;
    }

    [TestMethod]
    public void Converter_PreservesAssistantToolCallsAndToolResults()
    {
        using var args = JsonDocument.Parse("""{"path":"Foo.cs"}""");
        var ctx = new LoopContext("session-1");
        ctx.Messages.Add(new UserMessage([new TextBlock("read Foo")]));
        ctx.Messages.Add(new AssistantMessage([
            new TextBlock("I'll inspect it."),
            new ToolUseBlock("tu_1", "Read", args.RootElement.Clone())
        ]));
        ctx.Messages.Add(new UserMessage([
            new ToolResultBlock("tu_1", "file contents")
        ]));

        var messages = TrajectoryConverter.ToOpenAiMessages("system prompt", ctx);

        Assert.AreEqual("system", messages[0]["role"]!.GetValue<string>());
        Assert.AreEqual("user", messages[1]["role"]!.GetValue<string>());
        Assert.AreEqual("assistant", messages[2]["role"]!.GetValue<string>());
        Assert.AreEqual("tool", messages[3]["role"]!.GetValue<string>());
        Assert.AreEqual("tu_1", messages[3]["tool_call_id"]!.GetValue<string>());

        var toolCalls = messages[2]["tool_calls"]!.AsArray();
        Assert.AreEqual("tu_1", toolCalls[0]!["id"]!.GetValue<string>());
        Assert.AreEqual("Read", toolCalls[0]!["function"]!["name"]!.GetValue<string>());
        Assert.AreEqual("""{"path":"Foo.cs"}""", toolCalls[0]!["function"]!["arguments"]!.GetValue<string>());
    }

    [TestMethod]
    public void Converter_ConvertsToolSchemas()
    {
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{"path":{"type":"string"}}}""");

        var rows = TrajectoryConverter.ToToolRows([
            new ToolDefinition("Read", "Read a file", schema.RootElement.Clone())
        ]);

        Assert.AreEqual("Read", rows[0].Id);
        Assert.AreEqual("Read a file", rows[0].Description);
        Assert.AreEqual("object", rows[0].InputSchema.JsonSchema.GetProperty("type").GetString());
    }

    [TestMethod]
    public void Redactor_RedactsSecretsThroughoutNestedPayload()
    {
        var config = new DotsyConfig();
        config.Model.OpenAi.ApiKey = "sk-secretsecretsecret";
        var payload = new
        {
            agent_prompt = "key sk-secretsecretsecret",
            messages = new object[]
            {
                new { role = "assistant", tool_calls = new[] { new { arguments = """{"token":"sk-secretsecretsecret"}""" } } },
                new { role = "tool", content = "Authorization: Bearer abcdefghijklmnop" }
            },
            metadata = new { value = "sk-secretsecretsecret" }
        };

        var json = TrajectoryRedactor.Redact(payload, config);

        Assert.IsFalse(json.Contains("sk-secretsecretsecret"));
        Assert.IsFalse(json.Contains("abcdefghijklmnop"));
        Assert.IsTrue(json.Contains("[REDACTED]"));
    }

    [TestMethod]
    public void Recorder_WritesOneJsonFileWhenEnabled()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"dotsy_traj_{Guid.NewGuid():N}");
        try
        {
            using var schema = JsonDocument.Parse("""{"type":"object"}""");
            var config = new DotsyConfig();
            config.Trajectory.Enabled = true;
            config.Trajectory.Dir = tmp;
            var recorder = new TrajectoryRecorder(config, tmp);
            var request = new ChatRequest(
                "model",
                "system",
                [new UserMessage([new TextBlock("hello")])],
                [new ToolDefinition("Read", "Read", schema.RootElement.Clone())],
                1000);
            recorder.CaptureInitialRequest(request);
            recorder.RecordUsage(1, 2, 3, 4);
            var ctx = new LoopContext("abc");
            ctx.Messages.Add(new UserMessage([new TextBlock("hello")]));

            recorder.Export(ctx, EndReason.TaskComplete);

            var files = Directory.GetFiles(tmp, "*.json");
            Assert.AreEqual(1, files.Length);
            Assert.AreEqual("abc.json", Path.GetFileName(files[0]));
            Assert.AreEqual(0, Directory.GetFiles(tmp, "*.parquet").Length);

            using var json = JsonDocument.Parse(File.ReadAllText(files[0]));
            Assert.AreEqual("abc", json.RootElement.GetProperty("uuid").GetString());
            Assert.AreEqual("dotsy", json.RootElement.GetProperty("hf_split").GetString());
            Assert.AreEqual("completed", json.RootElement.GetProperty("metadata").GetProperty("outcome").GetString());
            Assert.AreEqual(1, json.RootElement.GetProperty("metadata").GetProperty("token_usage").GetProperty("input_tokens").GetInt32());
        }
        finally
        {
            if (Directory.Exists(tmp))
                Directory.Delete(tmp, recursive: true);
        }
    }

    [TestMethod]
    public void Recorder_DoesNotWriteWhenDisabled()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"dotsy_traj_{Guid.NewGuid():N}");
        using var schema = JsonDocument.Parse("""{"type":"object"}""");
        var config = new DotsyConfig();
        config.Trajectory.Enabled = false;
        config.Trajectory.Dir = tmp;
        var recorder = new TrajectoryRecorder(config, tmp);
        recorder.CaptureInitialRequest(new ChatRequest("model", "system", [], [new ToolDefinition("Read", "Read", schema.RootElement.Clone())], 1000));

        recorder.Export(new LoopContext("abc"), EndReason.TaskComplete);

        Assert.IsFalse(Directory.Exists(tmp));
    }

    [TestMethod]
    public async Task Integration_DisabledByDefaultWritesNoTrajectoryFile()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"dotsy_traj_integration_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var config = MakeIntegrationConfig(tmp, trajectoryEnabled: false);
            config.Trajectory = new TrajectoryConfig();

            await RunLoopAndExport(config, tmp);

            Assert.IsFalse(Directory.Exists(Path.Combine(tmp, ".dotsy", "trajectories")));
            Assert.IsFalse(Directory.Exists(Path.Combine(tmp, "trajectories")));
        }
        finally
        {
            if (Directory.Exists(tmp))
                Directory.Delete(tmp, recursive: true);
        }
    }

    [TestMethod]
    public async Task Integration_EnabledWritesOneJsonFileAndNoParquetArtifacts()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"dotsy_traj_integration_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var config = MakeIntegrationConfig(tmp, trajectoryEnabled: true);

            var ended = await RunLoopAndExport(config, tmp);

            Assert.AreEqual(EndReason.TurnLimitReached, ended.Reason);
            var trajectoryDir = config.Trajectory.Dir;
            var jsonFiles = Directory.GetFiles(trajectoryDir, "*.json");
            Assert.AreEqual(1, jsonFiles.Length);
            Assert.AreEqual("integration-session.json", Path.GetFileName(jsonFiles[0]));
            Assert.AreEqual(0, Directory.GetFiles(trajectoryDir, "*.parquet").Length);

            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(jsonFiles[0]));
            Assert.AreEqual("hello integration", json.RootElement.GetProperty("question").GetString());
            Assert.AreEqual("fake", json.RootElement.GetProperty("metadata").GetProperty("provider").GetString());
            Assert.AreEqual(11, json.RootElement.GetProperty("metadata").GetProperty("token_usage").GetProperty("input_tokens").GetInt32());
        }
        finally
        {
            if (Directory.Exists(tmp))
                Directory.Delete(tmp, recursive: true);
        }
    }

    [TestMethod]
    public async Task Integration_NoHistoryDisablesJsonlOnlyAndStillWritesTrajectory()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"dotsy_traj_integration_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var oldNoHistory = Environment.GetEnvironmentVariable("DOTSY_NO_HISTORY");
            Environment.SetEnvironmentVariable("DOTSY_NO_HISTORY", "1");
            try
            {
                var config = MakeIntegrationConfig(tmp, trajectoryEnabled: true);

                await RunLoopAndExport(config, tmp, noHistory: true);

                Assert.AreEqual(0, Directory.Exists(config.Session.LogDir)
                    ? Directory.GetFiles(config.Session.LogDir, "*.jsonl").Length
                    : 0);
                Assert.AreEqual(1, Directory.GetFiles(config.Trajectory.Dir, "*.json").Length);
                Assert.AreEqual(0, Directory.GetFiles(config.Trajectory.Dir, "*.parquet").Length);
            }
            finally
            {
                Environment.SetEnvironmentVariable("DOTSY_NO_HISTORY", oldNoHistory);
            }
        }
        finally
        {
            if (Directory.Exists(tmp))
                Directory.Delete(tmp, recursive: true);
        }
    }
}
