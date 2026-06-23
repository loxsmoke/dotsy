using System.Net;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Dotsy.Core.Config;
using Dotsy.Core.Loop;
using Dotsy.Core.Tools;
using Dotsy.Core.Tools.Interfaces;
using Dotsy.Core.Loop.Data;
using Dotsy.Core.Skills;

namespace Dotsy.Core.Tests;

[TestClass]
public sealed class ToolsTests
{
    private string _tmpDir = "";

    [TestInitialize]
    public void Setup()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"dotsy_tools_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
    }

    [TestCleanup]
    public void Teardown()
    {
        if (Directory.Exists(_tmpDir))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(_tmpDir, "*", SearchOption.AllDirectories))
                File.SetAttributes(path, FileAttributes.Normal);
            Directory.Delete(_tmpDir, recursive: true);
        }
    }

    private ToolContext Ctx(LoopContext? loopContext = null, Func<LoopEvent, Task>? emitEvent = null) => new()
    {
        Cwd = _tmpDir,
        LoopContext = loopContext ?? new LoopContext(),
        EmitEvent = emitEvent
    };

    private static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement;

    private static string JsonPath(string path) =>
        path.Replace("\\", "\\\\").Replace("\"", "\\\"");

    [TestMethod]
    public async Task WriteTool_CreatesParentDirectoriesAndMarksAsWriteTool()
    {
        var tool = new WriteTool();
        var result = await tool.ExecuteAsync(
            Args("""{"path":"nested/file.txt","content":"hello"}"""),
            Ctx(),
            CancellationToken.None);

        Assert.IsFalse(result.IsError, result.Content);
        Assert.IsTrue(tool.IsWriteTool);
        Assert.AreEqual("hello", await File.ReadAllTextAsync(Path.Combine(_tmpDir, "nested", "file.txt")));
    }

    [TestMethod]
    public async Task ReadTool_ReturnsNumberedLinesAndTruncationNotice()
    {
        await File.WriteAllTextAsync(Path.Combine(_tmpDir, "notes.txt"), "one\ntwo\nthree");

        var result = await new ReadTool().ExecuteAsync(
            Args("""{"path":"notes.txt","offset":1,"limit":1}"""),
            Ctx(),
            CancellationToken.None);

        Assert.IsFalse(result.IsError, result.Content);
        StringAssert.Contains(result.Content, "2 two");
        StringAssert.Contains(result.Content, "<truncated:");
    }

    [TestMethod]
    public async Task ReadTool_AcceptsStartLineEndLineRange()
    {
        await File.WriteAllTextAsync(Path.Combine(_tmpDir, "notes.txt"), "one\ntwo\nthree\nfour\nfive");

        var result = await new ReadTool().ExecuteAsync(
            Args("""{"path":"notes.txt","start_line":2,"end_line":3}"""),
            Ctx(),
            CancellationToken.None);

        Assert.IsFalse(result.IsError, result.Content);
        StringAssert.Contains(result.Content, "2 two");
        StringAssert.Contains(result.Content, "3 three");
        Assert.IsFalse(result.Content.Contains("1 one"), result.Content);
        Assert.IsFalse(result.Content.Contains("4 four"), result.Content);
    }

    [TestMethod]
    public async Task ReadTool_AcceptsStartLineEndLineAsStrings()
    {
        await File.WriteAllTextAsync(Path.Combine(_tmpDir, "notes.txt"), "one\ntwo\nthree\nfour\nfive");

        var result = await new ReadTool().ExecuteAsync(
            Args("""{"path":"notes.txt","start_line":"2","end_line":"3"}"""),
            Ctx(),
            CancellationToken.None);

        Assert.IsFalse(result.IsError, result.Content);
        StringAssert.Contains(result.Content, "2 two");
        StringAssert.Contains(result.Content, "3 three");
        Assert.IsFalse(result.Content.Contains("1 one"), result.Content);
        Assert.IsFalse(result.Content.Contains("4 four"), result.Content);
    }

    [TestMethod]
    public async Task ReadTool_OffsetLimitTakePrecedenceOverStartEndLine()
    {
        await File.WriteAllTextAsync(Path.Combine(_tmpDir, "notes.txt"), "one\ntwo\nthree\nfour\nfive");

        var result = await new ReadTool().ExecuteAsync(
            Args("""{"path":"notes.txt","offset":0,"limit":1,"start_line":3,"end_line":5}"""),
            Ctx(),
            CancellationToken.None);

        Assert.IsFalse(result.IsError, result.Content);
        StringAssert.Contains(result.Content, "1 one");
        Assert.IsFalse(result.Content.Contains("3 three"), result.Content);
    }

    [TestMethod]
    public async Task ReadTool_DoesNotExposeCarriageReturnsForCrLfFiles()
    {
        await File.WriteAllTextAsync(Path.Combine(_tmpDir, "windows.txt"), "one\r\ntwo\r\n");

        var result = await new ReadTool().ExecuteAsync(
            Args("""{"path":"windows.txt"}"""),
            Ctx(),
            CancellationToken.None);

        Assert.IsFalse(result.IsError, result.Content);
        Assert.IsFalse(result.Content.Contains('\r'), result.Content);
        StringAssert.Contains(result.Content, "1 one\n2 two\n");
    }

    [TestMethod]
    public async Task ReadTool_RejectsBinaryFiles()
    {
        await File.WriteAllBytesAsync(Path.Combine(_tmpDir, "bin.dat"), [1, 0, 2]);

        var result = await new ReadTool().ExecuteAsync(
            Args("""{"path":"bin.dat"}"""),
            Ctx(),
            CancellationToken.None);

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.Content, "Binary file");
    }

    [TestMethod]
    public async Task ReadTool_IncludeDiff_ReturnsGitDiffStatForRepoRoot()
    {
        if (!await GitSucceeds("init"))
            Assert.Inconclusive("git is not available");

        await File.WriteAllTextAsync(Path.Combine(_tmpDir, "tracked.txt"), "old\n");
        await GitSucceeds("config", "user.email", "test@example.com");
        await GitSucceeds("config", "user.name", "Dotsy Test");
        await GitSucceeds("add", "tracked.txt");
        await GitSucceeds("commit", "-m", "initial");
        await File.WriteAllTextAsync(Path.Combine(_tmpDir, "tracked.txt"), "old\nnew\n");

        var result = await new ReadTool().ExecuteAsync(
            Args("""{"path":".","include_diff":true}"""),
            Ctx(),
            CancellationToken.None);

        Assert.IsFalse(result.IsError, result.Content);
        StringAssert.Contains(result.Content, "<git_diff_stat>");
        StringAssert.Contains(result.Content, "tracked.txt");
    }

    [TestMethod]
    public async Task ListTool_ListsFilesAndDirectoriesRecursively()
    {
        Directory.CreateDirectory(Path.Combine(_tmpDir, "src"));
        await File.WriteAllTextAsync(Path.Combine(_tmpDir, "src", "app.cs"), "");
        await File.WriteAllTextAsync(Path.Combine(_tmpDir, "README.md"), "");

        var result = await new ListTool().ExecuteAsync(
            Args("""{"path":".","recursive":true}"""),
            Ctx(),
            CancellationToken.None);

        Assert.IsFalse(result.IsError, result.Content);
        StringAssert.Contains(result.Content.Replace('\\', '/'), "src/");
        StringAssert.Contains(result.Content.Replace('\\', '/'), "src/app.cs");
        StringAssert.Contains(result.Content, "README.md");
    }

    [TestMethod]
    public async Task GlobTool_ReturnsMatchingFilesOnly()
    {
        Directory.CreateDirectory(Path.Combine(_tmpDir, "src"));
        await File.WriteAllTextAsync(Path.Combine(_tmpDir, "src", "a.cs"), "");
        await File.WriteAllTextAsync(Path.Combine(_tmpDir, "src", "a.txt"), "");
        Directory.CreateDirectory(Path.Combine(_tmpDir, "src", "nested"));
        await File.WriteAllTextAsync(Path.Combine(_tmpDir, "src", "nested", "b.cs"), "");

        var result = await new GlobTool().ExecuteAsync(
            Args("""{"pattern":"*.cs","path":"src"}"""),
            Ctx(),
            CancellationToken.None);

        Assert.IsFalse(result.IsError, result.Content);
        StringAssert.Contains(result.Content, "a.cs");
        StringAssert.Contains(result.Content.Replace('\\', '/'), "nested/b.cs");
        Assert.IsFalse(result.Content.Contains("a.txt", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task GlobTool_SupportsRecursiveDoubleStarPattern()
    {
        await File.WriteAllTextAsync(Path.Combine(_tmpDir, "root.cs"), "");
        Directory.CreateDirectory(Path.Combine(_tmpDir, "src", "nested"));
        await File.WriteAllTextAsync(Path.Combine(_tmpDir, "src", "nested", "app.cs"), "");
        await File.WriteAllTextAsync(Path.Combine(_tmpDir, "src", "nested", "app.txt"), "");

        var result = await new GlobTool().ExecuteAsync(
            Args("""{"pattern":"**/*.cs"}"""),
            Ctx(),
            CancellationToken.None);

        Assert.IsFalse(result.IsError, result.Content);
        var content = result.Content.Replace('\\', '/');
        StringAssert.Contains(content, "root.cs");
        StringAssert.Contains(content, "src/nested/app.cs");
        Assert.IsFalse(content.Contains("app.txt", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task GlobTool_ReportsExactCountAndSkipsNoiseDirs()
    {
        await File.WriteAllTextAsync(Path.Combine(_tmpDir, "a.cs"), "");
        await File.WriteAllTextAsync(Path.Combine(_tmpDir, "b.cs"), "");
        Directory.CreateDirectory(Path.Combine(_tmpDir, "obj"));
        await File.WriteAllTextAsync(Path.Combine(_tmpDir, "obj", "Generated.cs"), "");
        Directory.CreateDirectory(Path.Combine(_tmpDir, "bin"));
        await File.WriteAllTextAsync(Path.Combine(_tmpDir, "bin", "X.cs"), "");

        var result = await new GlobTool().ExecuteAsync(
            Args("""{"pattern":"**/*.cs"}"""), Ctx(), CancellationToken.None);

        Assert.IsFalse(result.IsError, result.Content);
        StringAssert.Contains(result.Content, "2 file(s) matched.");
        Assert.IsFalse(result.Content.Contains("Generated.cs", StringComparison.Ordinal), "obj should be skipped");
        Assert.IsFalse(result.Content.Replace('\\', '/').Contains("bin/X.cs", StringComparison.Ordinal), "bin should be skipped");
    }

    [TestMethod]
    public async Task GlobTool_ExcludeParameterPrunesDirectory()
    {
        Directory.CreateDirectory(Path.Combine(_tmpDir, "src"));
        await File.WriteAllTextAsync(Path.Combine(_tmpDir, "src", "a.cs"), "");
        Directory.CreateDirectory(Path.Combine(_tmpDir, "extern"));
        await File.WriteAllTextAsync(Path.Combine(_tmpDir, "extern", "lib.cs"), "");

        var result = await new GlobTool().ExecuteAsync(
            Args("""{"pattern":"**/*.cs","exclude":"extern"}"""), Ctx(), CancellationToken.None);

        Assert.IsFalse(result.IsError, result.Content);
        StringAssert.Contains(result.Content, "1 file(s) matched.");
        Assert.IsFalse(result.Content.Contains("lib.cs", StringComparison.Ordinal), "extern should be excluded");
    }

    [TestMethod]
    public async Task FindDefsTool_ExtractsTypesAndMembers()
    {
        await File.WriteAllTextAsync(Path.Combine(_tmpDir, "Sample.cs"), """
            namespace Demo;

            public sealed class Widget
            {
                public string Name { get; set; } = "";
                public void Run() { }
            }
            """);

        var result = await new FindDefsTool().ExecuteAsync(
            Args("""{"path":"Sample.cs"}"""),
            Ctx(),
            CancellationToken.None);

        Assert.IsFalse(result.IsError, result.Content);
        StringAssert.Contains(result.Content, "public sealed class Widget");
        StringAssert.Contains(result.Content, "public string Name");
        StringAssert.Contains(result.Content, "public void Run(");
    }

    [TestMethod]
    public async Task FindDefsTool_AcceptsGlobPattern()
    {
        var sub = Path.Combine(_tmpDir, "src", "Tui");
        Directory.CreateDirectory(sub);
        await File.WriteAllTextAsync(Path.Combine(sub, "Palette.cs"),
            "namespace Demo; public static class Palette { }");

        // A glob passed as `path` (the mistake from the original failing prompt) must match.
        var result = await new FindDefsTool().ExecuteAsync(
            Args("""{"path":"**/*Palette.cs"}"""),
            Ctx(),
            CancellationToken.None);

        Assert.IsFalse(result.IsError, result.Content);
        StringAssert.Contains(result.Content, "static class Palette");
    }

    [TestMethod]
    public async Task FindDefsTool_SearchesForBareFileNameInSubdirectory()
    {
        var sub = Path.Combine(_tmpDir, "src", "Tui");
        Directory.CreateDirectory(sub);
        await File.WriteAllTextAsync(Path.Combine(sub, "Palette.cs"),
            "namespace Demo; public static class Palette { }");

        // A bare file name that lives in a subdirectory (not at the cwd) must be found.
        var result = await new FindDefsTool().ExecuteAsync(
            Args("""{"path":"Palette.cs"}"""),
            Ctx(),
            CancellationToken.None);

        Assert.IsFalse(result.IsError, result.Content);
        StringAssert.Contains(result.Content, "static class Palette");
    }

    [TestMethod]
    public async Task FindDefsTool_ReturnsActionableErrorWhenNothingMatches()
    {
        var result = await new FindDefsTool().ExecuteAsync(
            Args("""{"path":"**/*DoesNotExist.cs"}"""),
            Ctx(),
            CancellationToken.None);

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.Content, "Glob or Grep");
    }

    [TestMethod]
    public async Task DoneTool_ReturnsSummaryAndSignalsCompletion()
    {
        var tool = new DoneTool();
        var result = await tool.ExecuteAsync(
            Args("""{"summary":"finished"}"""),
            Ctx(),
            CancellationToken.None);

        Assert.IsTrue(tool.IsCompletionSignal);
        Assert.IsFalse(result.IsError, result.Content);
        Assert.AreEqual("finished", result.Content);
    }

    [TestMethod]
    public void DoneTool_FormatsPanelSummaryWithFiftyCharacterPreview()
    {
        var tool = new DoneTool();
        var shortSummary = tool.FormatPanelArgument(
            Args("""{"summary":"short summary"}"""),
            _tmpDir);
        var longSummary = tool.FormatPanelResult(
            Args("""{"summary":"ignored"}"""),
            "12345678901234567890123456789012345678901234567890extra text",
            _tmpDir);

        Assert.AreEqual("short summary", shortSummary);
        Assert.AreEqual("12345678901234567890123456789012345678901234567890...", longSummary);
    }

    private const string SampleTodo =
        "# Todo\n\n## 1. Improvements\n- [x] done one\n- [ ] todo one\n\n## 2. Bug fixes\n- [ ] todo two\n";

    [TestMethod]
    public async Task TodoTool_ListTasks_FiltersByStatus()
    {
        await File.WriteAllTextAsync(Path.Combine(_tmpDir, TodoTool.FileName), SampleTodo);

        var result = await new TodoTool().ExecuteAsync(
            Args("""{"action":"list_tasks","status":"todo"}"""),
            Ctx(),
            CancellationToken.None);

        Assert.IsFalse(result.IsError, result.Content);
        StringAssert.Contains(result.Content, "todo one");
        StringAssert.Contains(result.Content, "todo two");
        Assert.IsFalse(result.Content.Contains("done one"), result.Content);
    }

    [TestMethod]
    public async Task TodoTool_ListTasks_FiltersBySection()
    {
        await File.WriteAllTextAsync(Path.Combine(_tmpDir, TodoTool.FileName), SampleTodo);

        var result = await new TodoTool().ExecuteAsync(
            Args("""{"action":"list_tasks","status":"all","section":"2"}"""),
            Ctx(),
            CancellationToken.None);

        Assert.IsFalse(result.IsError, result.Content);
        StringAssert.Contains(result.Content, "todo two");
        Assert.IsFalse(result.Content.Contains("todo one"), result.Content);
    }

    [TestMethod]
    public async Task TodoTool_SetStatus_FlipsCheckboxInFile()
    {
        var path = Path.Combine(_tmpDir, TodoTool.FileName);
        await File.WriteAllTextAsync(path, SampleTodo);

        // Task index 2 is "[ ] todo one".
        var result = await new TodoTool().ExecuteAsync(
            Args("""{"action":"set_status","task":2,"done":true}"""),
            Ctx(),
            CancellationToken.None);

        Assert.IsFalse(result.IsError, result.Content);
        var updated = await File.ReadAllTextAsync(path);
        StringAssert.Contains(updated, "- [x] todo one");
        StringAssert.Contains(updated, "- [ ] todo two"); // others untouched
    }

    [TestMethod]
    public async Task TodoTool_ListSections_ReportsCounts()
    {
        await File.WriteAllTextAsync(Path.Combine(_tmpDir, TodoTool.FileName), SampleTodo);

        var result = await new TodoTool().ExecuteAsync(
            Args("""{"action":"list_sections"}"""),
            Ctx(),
            CancellationToken.None);

        Assert.IsFalse(result.IsError, result.Content);
        StringAssert.Contains(result.Content, "1. Improvements (1 todo, 1 done)");
        StringAssert.Contains(result.Content, "2. Bug fixes (1 todo, 0 done)");
    }

    [TestMethod]
    public async Task TodoTool_MissingFile_ReturnsError()
    {
        var result = await new TodoTool().ExecuteAsync(
            Args("""{"action":"list_tasks"}"""),
            Ctx(),
            CancellationToken.None);

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.Content, "not found");
    }

    [TestMethod]
    public async Task AskTool_EmitsPermissionEventAndAcknowledgesAllowDecision()
    {
        PermissionRequired? captured = null;
        var ctx = Ctx();
        ctx = new ToolContext
        {
            Cwd = ctx.Cwd,
            LoopContext = ctx.LoopContext,
            EmitEvent = ev =>
            {
                captured = (PermissionRequired)ev;
                captured.Decision.SetResult(PermissionDecision.AllowOnce);
                return Task.CompletedTask;
            }
        };

        var result = await new AskTool().ExecuteAsync(
            Args("""{"question":"continue?"}"""),
            ctx,
            CancellationToken.None);

        Assert.IsFalse(result.IsError, result.Content);
        Assert.AreEqual("Ask", captured?.ToolName);
        Assert.AreEqual("continue?", captured?.DisplayArgument);
    }

    [TestMethod]
    public async Task TaskTool_UsesLaunchDelegate()
    {
        var tool = new TaskTool
        {
            LaunchSubTask = (description, prompt, _) =>
                Task.FromResult($"{description}:{prompt}")
        };

        var result = await tool.ExecuteAsync(
            Args("""{"description":"scan","prompt":"find docs"}"""),
            Ctx(),
            CancellationToken.None);

        Assert.IsFalse(result.IsError, result.Content);
        Assert.AreEqual("task_id=scan:find docs", result.Content);
    }

    [TestMethod]
    public async Task TaskTool_UsesStatusDelegateForTaskId()
    {
        var tool = new TaskTool
        {
            GetSubTaskStatus = (taskId, _) => Task.FromResult($"status for {taskId}")
        };

        var result = await tool.ExecuteAsync(
            Args("""{"task_id":"task-1"}"""),
            Ctx(),
            CancellationToken.None);

        Assert.IsFalse(result.IsError, result.Content);
        Assert.AreEqual("status for task-1", result.Content);
    }

    [TestMethod]
    public async Task SkillTool_LoadsLocalSkillAndRecordsItInLoopContext()
    {
        await WriteSkill("helper", "Use the helper skill.");

        var loopContext = new LoopContext();
        var discovery = new SkillDiscovery(new SkillsConfig(), _tmpDir);
        var result = await new SkillTool(discovery).ExecuteAsync(
            Args("""{"name":"helper"}"""),
            Ctx(loopContext, ev =>
            {
                ((PermissionRequired)ev).Decision.SetResult(PermissionDecision.AllowOnce);
                return Task.CompletedTask;
            }),
            CancellationToken.None);

        Assert.IsFalse(result.IsError, result.Content);
        StringAssert.Contains(result.Content, "<skill_content name=\"helper\"");
        Assert.AreEqual("Use the helper skill.", loopContext.LoadedSkills["helper"].Trim());
    }

    [TestMethod]
    public async Task SkillTool_DeniesFirstUnapprovedSkillLoad()
    {
        await WriteSkill("helper", "Use the helper skill.");

        var discovery = new SkillDiscovery(new SkillsConfig(), _tmpDir);
        var result = await new SkillTool(discovery).ExecuteAsync(
            Args("""{"name":"helper"}"""),
            Ctx(emitEvent: ev =>
            {
                ((PermissionRequired)ev).Decision.SetResult(PermissionDecision.Deny);
                return Task.CompletedTask;
            }),
            CancellationToken.None);

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.Content, "Permission denied");
    }

    [TestMethod]
    public async Task SkillTool_AsksOnlyOncePerSkillPerSession()
    {
        await WriteSkill("helper", "Use the helper skill.");

        var loopContext = new LoopContext();
        var discovery = new SkillDiscovery(new SkillsConfig(), _tmpDir);
        var tool = new SkillTool(discovery);
        var promptCount = 0;
        var ctx = Ctx(loopContext, ev =>
        {
            promptCount++;
            ((PermissionRequired)ev).Decision.SetResult(PermissionDecision.AllowOnce);
            return Task.CompletedTask;
        });

        var first = await tool.ExecuteAsync(Args("""{"name":"helper"}"""), ctx, CancellationToken.None);
        var second = await tool.ExecuteAsync(Args("""{"name":"helper"}"""), ctx, CancellationToken.None);

        Assert.IsFalse(first.IsError, first.Content);
        Assert.IsFalse(second.IsError, second.Content);
        Assert.AreEqual(1, promptCount);
    }

    [TestMethod]
    public async Task SkillDiscovery_LoadsDirectorySkillAndCompanions()
    {
        var skillDir = Path.Combine(_tmpDir, ".dotsy", "skills", "planner");
        Directory.CreateDirectory(skillDir);
        await File.WriteAllTextAsync(Path.Combine(skillDir, "SKILL.md"), """
            ---
            name: planner
            description: Plan work.
            allowed-tools: [Read, Grep]
            ---
            Use the planner skill.
            """);
        await File.WriteAllTextAsync(Path.Combine(skillDir, "example.txt"), "companion");

        var record = new SkillDiscovery(new SkillsConfig(), _tmpDir).Find("planner");
        Assert.IsNotNull(record);

        var skill = SkillLoader.Load(record);
        Assert.AreEqual("planner", skill.Frontmatter.Name);
        Assert.AreEqual("Plan work.", skill.Frontmatter.Description);
        CollectionAssert.AreEqual(new[] { "Read", "Grep" }, skill.Frontmatter.AllowedTools);
        StringAssert.Contains(skill.Body, "Use the planner skill.");
        Assert.IsTrue(skill.CompanionPaths.Any(p => Path.GetFileName(p) == "example.txt"));
    }

    [TestMethod]
    public async Task SkillDiscovery_LoadsProjectAgentsSkills()
    {
        var skillDir = Path.Combine(_tmpDir, ".agents", "skills");
        Directory.CreateDirectory(skillDir);
        await File.WriteAllTextAsync(Path.Combine(skillDir, "agent-helper.md"), """
            ---
            name: agent-helper
            ---
            Use agent helper.
            """);

        var record = new SkillDiscovery(new SkillsConfig(), _tmpDir).Find("agent-helper");

        Assert.IsNotNull(record);
        StringAssert.Contains(record.Body, "Use agent helper.");
    }

    [TestMethod]
    public async Task SkillTool_RejectsDisableModelInvocationSkill()
    {
        var skillDir = Path.Combine(_tmpDir, ".dotsy", "skills");
        Directory.CreateDirectory(skillDir);
        await File.WriteAllTextAsync(Path.Combine(skillDir, "manual.md"), """
            ---
            name: manual
            disable-model-invocation: true
            ---
            Load only by slash command.
            """);

        var result = await new SkillTool(new SkillDiscovery(new SkillsConfig(), _tmpDir)).ExecuteAsync(
            Args("""{"name":"manual"}"""),
            Ctx(emitEvent: ev =>
            {
                ((PermissionRequired)ev).Decision.SetResult(PermissionDecision.AllowOnce);
                return Task.CompletedTask;
            }),
            CancellationToken.None);

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.Content, "requires explicit /skill command");
    }

    private async Task WriteSkill(string name, string body)
    {
        var skillDir = Path.Combine(_tmpDir, ".dotsy", "skills");
        Directory.CreateDirectory(skillDir);
        await File.WriteAllTextAsync(Path.Combine(skillDir, $"{name}.md"), $$"""
            ---
            name: {{name}}
            ---
            {{body}}
            """);
    }

    private async Task<bool> GitSucceeds(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = _tmpDir,
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

    [TestMethod]
    public void ToolRegistry_RegistersBuiltInsAndLogsMcpShadowing()
    {
        var logs = new List<string>();
        var registry = ToolRegistry.CreateWithBuiltIns(new SkillDiscovery(new SkillsConfig(), _tmpDir), log: logs.Add);

        Assert.IsTrue(registry.TryGetTool("write", out var writeTool));
        Assert.IsNotNull(writeTool);
        Assert.IsTrue(writeTool.IsWriteTool);

        registry.RegisterMcpTool(new TestTool("Write"));

        Assert.AreEqual(1, logs.Count);
        StringAssert.Contains(logs[0], "shadows a built-in tool");
    }

    [TestMethod]
    public async Task WebFetchTool_ConvertsHtmlToMarkdown()
    {
        using var http = new HttpClient(new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html><body><h1>Title</h1><p>Hello <strong>there</strong></p></body></html>", Encoding.UTF8, "text/html")
        }));

        var result = await new WebFetchTool(http).ExecuteAsync(
            Args("""{"url":"https://example.test"}"""),
            Ctx(),
            CancellationToken.None);

        Assert.IsFalse(result.IsError, result.Content);
        StringAssert.Contains(result.Content, "# Title");
        StringAssert.Contains(result.Content, "Hello there");
    }

    [TestMethod]
    public async Task WebSearchTool_ReturnsAbstractAndRelatedTopics()
    {
        using var http = new HttpClient(new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {
                  "AbstractText": "Dotsy is a test subject.",
                  "AbstractURL": "https://example.test/dotsy",
                  "RelatedTopics": [
                    { "Text": "Dotsy topic", "FirstURL": "https://example.test/topic" }
                  ]
                }
                """, Encoding.UTF8, "application/json")
        }));

        var result = await new WebSearchTool(http).ExecuteAsync(
            Args("""{"query":"dotsy"}"""),
            Ctx(),
            CancellationToken.None);

        Assert.IsFalse(result.IsError, result.Content);
        StringAssert.Contains(result.Content, "**Dotsy is a test subject.**");
        StringAssert.Contains(result.Content, "- Dotsy topic");
    }

    [TestMethod]
    public void ToolPanelFormatting_FormatsArgumentsAndResults()
    {
        var write = new WriteTool().FormatPanelArgument(
            Args("""{"path":"src/file.txt","content":"one\ntwo"}"""),
            _tmpDir);
        var readResult = new ReadTool().FormatPanelResult(
            Args("""{"path":"src/file.txt"}"""),
            "1\tone\n2\ttwo\n",
            _tmpDir);

        Assert.AreEqual($"src{Path.DirectorySeparatorChar}file.txt  +2 lines", write);
        Assert.AreEqual($"src{Path.DirectorySeparatorChar}file.txt  2 lines", readResult);
    }

    [TestMethod]
    public void TodoTool_FormatsPanelSummary()
    {
        var sections = new TodoTool().FormatPanelArgument(
            Args("""{"action":"list_sections"}"""),
            _tmpDir);
        var setStatus = new TodoTool().FormatPanelArgument(
            Args("""{"action":"set_status","task":2,"done":true}"""),
            _tmpDir);

        Assert.AreEqual("List sections", sections);
        Assert.AreEqual("Set task 2 done", setStatus);
    }

    [TestMethod]
    public void EditTool_FormatsInspectionOutputWithInput()
    {
        var formatted = new EditTool().FormatPanelResult(
            Args("""{"path":"src/file.txt","start_line":2,"end_line":3,"new_string":"new text"}"""),
            "Edited: src/file.txt",
            _tmpDir);

        StringAssert.Contains(formatted!, "Output");
        StringAssert.Contains(formatted!, "Edited: src/file.txt");
        StringAssert.Contains(formatted!, "Input");
        StringAssert.Contains(formatted!, "start_line: 2");
        StringAssert.Contains(formatted!, "end_line: 3");
        StringAssert.Contains(formatted!, "new_string:");
        StringAssert.Contains(formatted!, "new text");
    }

    [TestMethod]
    public void EditSchemas_ExposeOnlyLineRangeInputs()
    {
        var editSchema = new EditTool().InputSchema.ToString();
        var multiEditSchema = new MultiEditTool().InputSchema.ToString();

        StringAssert.Contains(editSchema, "start_line");
        StringAssert.Contains(editSchema, "end_line");
        StringAssert.Contains(multiEditSchema, "start_line");
        StringAssert.Contains(multiEditSchema, "end_line");
        Assert.IsFalse(editSchema.Contains("old_string", StringComparison.Ordinal), editSchema);
        Assert.IsFalse(editSchema.Contains("replace_all", StringComparison.Ordinal), editSchema);
        Assert.IsFalse(multiEditSchema.Contains("old_string", StringComparison.Ordinal), multiEditSchema);
        Assert.IsFalse(multiEditSchema.Contains("replace_all", StringComparison.Ordinal), multiEditSchema);
    }

    [TestMethod]
    public async Task MultiEditTool_AcceptsStringEncodedEditsArray()
    {
        await File.WriteAllTextAsync(Path.Combine(_tmpDir, "f.txt"), "hello world");

        // Some models double-encode `edits` as a JSON string instead of an array.
        var editsArray = """[{"old_string":"world","new_string":"there"}]""";
        var inputJson = JsonSerializer.Serialize(new { path = "f.txt", edits = editsArray });

        var result = await new MultiEditTool().ExecuteAsync(Args(inputJson), Ctx(), CancellationToken.None);

        Assert.IsFalse(result.IsError, result.Content);
        Assert.AreEqual("hello there", await File.ReadAllTextAsync(Path.Combine(_tmpDir, "f.txt")));
    }

    [TestMethod]
    public async Task MultiEditTool_AcceptsDoubleEncodedEditsString()
    {
        await File.WriteAllTextAsync(Path.Combine(_tmpDir, "f.txt"), "hello world");

        var arrayText = """[{"old_string":"world","new_string":"there"}]""";
        var doubleEncoded = JsonSerializer.Serialize(arrayText); // a string that parses to the array string
        var inputJson = JsonSerializer.Serialize(new { path = "f.txt", edits = doubleEncoded });

        var result = await new MultiEditTool().ExecuteAsync(Args(inputJson), Ctx(), CancellationToken.None);

        Assert.IsFalse(result.IsError, result.Content);
        Assert.AreEqual("hello there", await File.ReadAllTextAsync(Path.Combine(_tmpDir, "f.txt")));
    }

    [TestMethod]
    public async Task MultiEditTool_GivesClearErrorWhenEditsIsNotAnArray()
    {
        await File.WriteAllTextAsync(Path.Combine(_tmpDir, "f.txt"), "hello");
        var inputJson = JsonSerializer.Serialize(new { path = "f.txt", edits = "not an array" });

        var result = await new MultiEditTool().ExecuteAsync(Args(inputJson), Ctx(), CancellationToken.None);

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.Content, "edits must be an array");
        Assert.AreEqual("hello", await File.ReadAllTextAsync(Path.Combine(_tmpDir, "f.txt")), "file untouched on error");
    }

    [TestMethod]
    public void ToolFormatRunApproval_FormatsWriteAndDefaultsToRawJson()
    {
        var path = Path.Combine(_tmpDir, "existing.txt");
        File.WriteAllText(path, "old");

        var formatted = new WriteTool().FormatRunApproval(
            Args($$"""{"path":"{{JsonPath(path)}}","content":"new\ncontent"}"""),
            _tmpDir);
        var fallback = ((ITool)new TestTool("Test")).FormatRunApproval(
            Args("""{"value":"raw"}"""),
            _tmpDir);

        StringAssert.Contains(formatted, "existing.txt");
        StringAssert.Contains(formatted, "2 lines");
        StringAssert.Contains(formatted, "(was 1)");
        Assert.AreEqual("""{"value":"raw"}""", fallback);
    }

    [TestMethod]
    public async Task ShellTool_ReturnsOutputForSuccessfulCommand()
    {
        var result = await new ShellTool().ExecuteAsync(
            Args("""{"command":"dotnet --version","timeout_ms":5000}"""),
            Ctx(),
            CancellationToken.None);

        Assert.IsFalse(result.IsError, result.Content);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Content));
    }

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_respond(request));
    }

    private sealed class TestTool : ITool
    {
        public TestTool(string name)
        {
            Name = name;
        }

        public string Name { get; }
        public string Description => "Test tool";
        public JsonElement InputSchema => Args("""{"type":"object"}""");
        public ToolSafety Safety => ToolSafety.ReadOnly;
        public bool IsCompletionSignal => false;

        public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext ctx, CancellationToken ct) =>
            Task.FromResult(ToolResult.Ok(""));
    }
}
