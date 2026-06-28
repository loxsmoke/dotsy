using System.Text;
using Dotsy.Cli.SlashCommands;
using Dotsy.Cli.SlashCommands.Interfaces;
using Dotsy.Cli.Tui;
using Dotsy.Core.Config;
using Dotsy.Core.Session.Data;

namespace Dotsy.Cli.Tests;

[TestClass]
public sealed class VerboseCommandTests
{
    [TestMethod]
    [DataRow("true", true)]
    [DataRow("TRUE", true)]
    [DataRow("false", false)]
    [DataRow("FALSE", false)]
    public void Execute_SetsExplicitValue(string argument, bool expected)
    {
        var previousConfig = TuiSessionContext.Config;
        try
        {
            TuiSessionContext.Config = new DotsyConfig();
            new VerboseCommand().Execute(new CapturingHost(), argument);

            Assert.AreEqual(expected, TuiSessionContext.Config.Tui.Verbose);
        }
        finally
        {
            TuiSessionContext.Config = previousConfig;
        }
    }

    [TestMethod]
    public void Execute_InvalidValue_DoesNotChangeSetting()
    {
        var previousConfig = TuiSessionContext.Config;
        try
        {
            TuiSessionContext.Config = new DotsyConfig();
            TuiSessionContext.Config.Tui.Verbose = true;
            var host = new CapturingHost();

            new VerboseCommand().Execute(host, "yes");

            Assert.IsTrue(TuiSessionContext.Config.Tui.Verbose);
            StringAssert.Contains(host.Output, "usage: /verbose [true|false]");
        }
        finally
        {
            TuiSessionContext.Config = previousConfig;
        }
    }

    [TestMethod]
    public void Complete_OffersBooleanValues()
    {
        var command = new VerboseCommand();

        CollectionAssert.AreEqual(
            new[] { "/verbose true", "/verbose false" },
            command.Complete(new CapturingHost(), "").Select(item => item.Replacement).ToArray());
        CollectionAssert.AreEqual(
            new[] { "/verbose false" },
            command.Complete(new CapturingHost(), "f").Select(item => item.Replacement).ToArray());
    }

    private sealed class CapturingHost : ISlashCommandHost
    {
        private readonly StringBuilder output = new();
        public string Output => output.ToString();
        public IReadOnlyList<SlashCommandUsage> CommandUsages => [];
        public bool IsBusy => false;
        public void Write(string text, Terminal.Gui.Drawing.Attribute color) => output.Append(text);
        public void WriteError(string message) => output.Append(message);
        public void WriteDescription(int nameWidth, string name, string description, Terminal.Gui.Drawing.Attribute? nameColor = null) { }
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
