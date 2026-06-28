using System.Text;
using System.Text.Json;
using Dotsy.Cli.SlashCommands;
using Dotsy.Cli.SlashCommands.Interfaces;
using Dotsy.Cli.Tui;
using Dotsy.Core.Config;
using Dotsy.Core.Session.Data;

namespace Dotsy.Cli.Tests;

[TestClass]
public sealed class ResumeCommandTests
{
    private string _tmpDir = "";

    [TestInitialize]
    public void Setup()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"dotsy_resume_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
    }

    [TestCleanup]
    public void Teardown()
    {
        if (Directory.Exists(_tmpDir))
            Directory.Delete(_tmpDir, recursive: true);
    }

    [TestMethod]
    public void List_ShowsFiveLatestSessionsAndTotalForCurrentCwd()
    {
        var cwd = Path.Combine(_tmpDir, "work");
        var sessionDir = Path.Combine(_tmpDir, "sessions");
        Directory.CreateDirectory(cwd);
        Directory.CreateDirectory(sessionDir);

        WriteIndex(sessionDir, cwd, 6);
        for (var i = 1; i <= 6; i++)
            WriteSessionFile(sessionDir, $"20260624.{i}", userSteps: i);

        var previousConfig = TuiSessionContext.Config;
        var previousCwd = TuiSessionContext.Cwd;
        try
        {
            TuiSessionContext.Config = new DotsyConfig
            {
                Session = new SessionConfig { LogDir = sessionDir }
            };
            TuiSessionContext.Cwd = cwd;

            var host = new CapturingHost();
            new ResumeCommand().Execute(host, "list");

            var output = host.Output;
            StringAssert.Contains(output, "Recent sessions: showing 5 of 6");
            StringAssert.Contains(output, "ID          Ago       Steps Model              Title");
            Assert.IsFalse(output.Contains("Last command"), output);
            Assert.IsFalse(output.Contains("2026-06-24 12:"), output);
            StringAssert.Contains(output, "20260624.6");
            StringAssert.Contains(output, "20260624.2");
            Assert.IsFalse(output.Contains("20260624.1"), output);
        }
        finally
        {
            TuiSessionContext.Config = previousConfig;
            TuiSessionContext.Cwd = previousCwd;
        }
    }

    [TestMethod]
    public void RegistryHelp_IncludesResumeListUsage()
    {
        var usages = SlashCommandRegistry.CreateDefault().Usages;

        Assert.IsTrue(usages.Any(u => u.Syntax == "/resume list"));
    }

    [TestMethod]
    public void Complete_TopLevelShowsListAndSelectSession()
    {
        var items = new ResumeCommand().Complete(new CapturingHost(), "");

        CollectionAssert.AreEqual(
            new[] { "/resume list", "/resume select session " },
            items.Select(i => i.Replacement).ToArray());
    }

    [TestMethod]
    public void Complete_SelectSessionDrillsIntoDaysAndSessionIds()
    {
        var cwd = Path.Combine(_tmpDir, "work");
        var sessionDir = Path.Combine(_tmpDir, "sessions");
        Directory.CreateDirectory(cwd);
        Directory.CreateDirectory(sessionDir);
        WriteIndex(sessionDir, cwd, 3);

        var previousConfig = TuiSessionContext.Config;
        var previousCwd = TuiSessionContext.Cwd;
        try
        {
            TuiSessionContext.Config = new DotsyConfig
            {
                Session = new SessionConfig { LogDir = sessionDir }
            };
            TuiSessionContext.Cwd = cwd;

            var command = new ResumeCommand();
            var days = command.Complete(new CapturingHost(), "select session ");
            CollectionAssert.AreEqual(
                new[] { "/resume select session 2026-06-24 " },
                days.Select(i => i.Replacement).ToArray());

            var sessions = command.Complete(new CapturingHost(), "select session 2026-06-24 ");
            CollectionAssert.AreEqual(
                new[] { "/resume 20260624.3", "/resume 20260624.2", "/resume 20260624.1" },
                sessions.Select(i => i.Replacement).ToArray());
        }
        finally
        {
            TuiSessionContext.Config = previousConfig;
            TuiSessionContext.Cwd = previousCwd;
        }
    }

    [TestMethod]
    public void Execute_LoadsSessionAndRequestsHistoryReplay()
    {
        var cwd = Path.Combine(_tmpDir, "work");
        var sessionDir = Path.Combine(_tmpDir, "sessions");
        Directory.CreateDirectory(cwd);
        Directory.CreateDirectory(sessionDir);
        WriteIndex(sessionDir, cwd, 1);
        WriteSessionFile(sessionDir, "20260624.1", userSteps: 1);

        var previousConfig = TuiSessionContext.Config;
        var previousCwd = TuiSessionContext.Cwd;
        var previousLoopCtx = TuiSessionContext.LoopCtx;
        var previousSession = TuiSessionContext.Session;
        var previousLoopFactory = TuiSessionContext.LoopFactory;
        try
        {
            TuiSessionContext.Config = new DotsyConfig
            {
                Session = new SessionConfig { LogDir = sessionDir }
            };
            TuiSessionContext.Cwd = cwd;
            TuiSessionContext.LoopFactory = null;

            var host = new CapturingHost();
            new ResumeCommand().Execute(host, "20260624.1");

            Assert.AreEqual("20260624.1", host.RenderedSessionId);
            Assert.AreEqual("20260624.1", host.SetSessionId);
            Assert.AreEqual("20260624.1", TuiSessionContext.LoopCtx?.SessionId);
            Assert.AreEqual(1, TuiSessionContext.LoopCtx?.Messages.Count);
        }
        finally
        {
            TuiSessionContext.Config = previousConfig;
            TuiSessionContext.Cwd = previousCwd;
            TuiSessionContext.LoopCtx = previousLoopCtx;
            TuiSessionContext.Session = previousSession;
            TuiSessionContext.LoopFactory = previousLoopFactory;
        }
    }

    private static void WriteIndex(string sessionDir, string cwd, int count)
    {
        var root = Path.GetDirectoryName(sessionDir)!;
        var sessions = Enumerable.Range(1, count)
            .Select(i => new
            {
                sessionId = $"20260624.{i}",
                title = $"session {i}",
                cwd,
                model = "test-model",
                createdAt = new DateTimeOffset(2026, 6, 24, 10, i, 0, TimeSpan.Zero),
                updatedAt = new DateTimeOffset(2026, 6, 24, 11, i, 0, TimeSpan.Zero),
                messageCount = i * 10
            })
            .ToArray();

        File.WriteAllText(
            Path.Combine(root, "sessions.json"),
            JsonSerializer.Serialize(new { sessions }));
    }

    private static void WriteSessionFile(string sessionDir, string sessionId, int userSteps)
    {
        var records = Enumerable.Range(1, userSteps)
            .Select(i => JsonSerializer.Serialize(new
            {
                sessionId,
                type = "user",
                timestamp = new DateTimeOffset(2026, 6, 24, 12, i, 0, TimeSpan.Zero),
                cwd = "ignored",
                message = new { content = $"prompt {i}" }
            }));

        File.WriteAllLines(Path.Combine(sessionDir, $"{sessionId}.jsonl"), records);
    }

    private sealed class CapturingHost : ISlashCommandHost
    {
        private readonly StringBuilder _output = new();

        public string Output => _output.ToString();
        public string? RenderedSessionId { get; private set; }
        public string? SetSessionId { get; private set; }
        public IReadOnlyList<SlashCommandUsage> CommandUsages => [];
        public bool IsBusy => false;

        public void Write(string text, Terminal.Gui.Drawing.Attribute color) => _output.Append(text);
        public void WriteError(string message) => _output.Append(message);
        public void WriteDescription(
            int nameWidth,
            string name,
            string description,
            Terminal.Gui.Drawing.Attribute? nameColor = null) =>
            _output.Append(name).Append(' ').Append(description).AppendLine();

        public void SetState(string state) { }
        public void SetModel(string id) { }
        public void SetSession(string id) => SetSessionId = id;
        public void UpdateStatusBarFromCtx() { }
        public void StartSpinner(string state) { }
        public void StopSpinner(string state) { }
        public void ResetConversationView() { }
        public void ResetToolAndFilePanels() { }
        public void RefreshChangedFiles() { }
        public void RenderLoadedSession(LoadedSession loaded) => RenderedSessionId = loaded.SessionId;
        public (string Resolved, bool FellBack) ApplyTheme(string value) => (value, false);
        public void SubmitUserPrompt(string displayText, string promptText) { }
        public void AddPromptHistory(string entry) { }
        public CancellationToken BeginScenario() => CancellationToken.None;
        public void EndScenario() { }
        public void RequestStop() { }
        public void Invoke(Action action) => action();
    }
}
