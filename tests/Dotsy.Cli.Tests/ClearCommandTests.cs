using System.Text;
using Dotsy.Cli.SlashCommands;
using Dotsy.Cli.SlashCommands.Interfaces;
using Dotsy.Cli.Tui;
using Dotsy.Core.Config;
using Dotsy.Core.Session.Data;

namespace Dotsy.Cli.Tests;

[TestClass]
public sealed class ClearCommandTests
{
    private string _tmpDir = "";

    [TestInitialize]
    public void Setup()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"dotsy_clear_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
    }

    [TestCleanup]
    public void Teardown()
    {
        if (Directory.Exists(_tmpDir))
            Directory.Delete(_tmpDir, recursive: true);
    }

    // /clear must rebuild the AgentLoop: the loop captures its SessionStore at construction, so
    // keeping the old instance splits the log — prompts go to the new session file while
    // assistant/tool records keep landing in the previous one (observed in webcam-sec dogfooding).

    [TestMethod]
    public void Execute_SwapsSessionAndRebuildsLoopViaFactory()
    {
        var cwd = Path.Combine(_tmpDir, "work");
        var sessionDir = Path.Combine(_tmpDir, "sessions");
        Directory.CreateDirectory(cwd);
        Directory.CreateDirectory(sessionDir);

        var previousConfig = TuiSessionContext.Config;
        var previousCwd = TuiSessionContext.Cwd;
        var previousLoopCtx = TuiSessionContext.LoopCtx;
        var previousSession = TuiSessionContext.Session;
        var previousLoopFactory = TuiSessionContext.LoopFactory;
        var previousLoop = TuiSessionContext.Loop;
        try
        {
            TuiSessionContext.Config = new DotsyConfig
            {
                Session = new SessionConfig { LogDir = sessionDir }
            };
            TuiSessionContext.Cwd = cwd;
            TuiSessionContext.Loop = null;

            var factoryInvoked = false;
            TuiSessionContext.LoopFactory = () =>
            {
                factoryInvoked = true;
                return null!;   // the command only needs to invoke and assign it
            };

            new ClearCommand().Execute(new CapturingHost(), "");

            Assert.IsTrue(factoryInvoked, "/clear must rebuild the loop so it logs to the new session store");
            Assert.IsNotNull(TuiSessionContext.Session);
            Assert.IsNotNull(TuiSessionContext.LoopCtx);
            Assert.AreEqual(TuiSessionContext.Session!.SessionId, TuiSessionContext.LoopCtx!.SessionId);
        }
        finally
        {
            TuiSessionContext.Config = previousConfig;
            TuiSessionContext.Cwd = previousCwd;
            TuiSessionContext.LoopCtx = previousLoopCtx;
            TuiSessionContext.Session = previousSession;
            TuiSessionContext.LoopFactory = previousLoopFactory;
            TuiSessionContext.Loop = previousLoop;
        }
    }

    [TestMethod]
    public void Execute_WithoutFactory_StillSwapsSession()
    {
        var cwd = Path.Combine(_tmpDir, "work");
        var sessionDir = Path.Combine(_tmpDir, "sessions");
        Directory.CreateDirectory(cwd);
        Directory.CreateDirectory(sessionDir);

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

            var before = TuiSessionContext.Session;
            new ClearCommand().Execute(new CapturingHost(), "");

            Assert.AreNotSame(before, TuiSessionContext.Session);
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

    private sealed class CapturingHost : ISlashCommandHost
    {
        private readonly StringBuilder _output = new();

        public string Output => _output.ToString();
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
        public void SetSession(string id) { }
        public void UpdateStatusBarFromCtx() { }
        public void StartSpinner(string state) { }
        public void StopSpinner(string state) { }
        public void ResetConversationView() { }
        public void ResetToolAndFilePanels() { }
        public void RefreshChangedFiles() { }
        public void RenderLoadedSession(LoadedSession loaded) { }
        public (string Resolved, bool FellBack) ApplyTheme(string value) => (value, false);
        public void SubmitUserPrompt(string displayText, string promptText) { }
        public void AddPromptHistory(string entry) { }
        public CancellationToken BeginScenario() => CancellationToken.None;
        public void EndScenario() { }
        public void RequestStop() { }
        public void Invoke(Action action) => action();
    }
}
