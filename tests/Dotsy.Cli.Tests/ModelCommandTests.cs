using System.Text;
using Dotsy.Cli.SlashCommands;
using Dotsy.Cli.SlashCommands.Interfaces;
using Dotsy.Cli.Tui;
using Dotsy.Core.Config;
using Dotsy.Core.Providers;
using Dotsy.Core.Session.Data;

namespace Dotsy.Cli.Tests;

[TestClass]
public sealed class ModelCommandTests
{
    [TestMethod]
    public void Complete_LoadsModelsFromCurrentProviderAndCachesForSession()
    {
        var provider = ProviderConfig.Ollama;
        var previousConfig = TuiSessionContext.Config;
        var previousLookup = TuiSessionContext.ModelListLookup;
        var calls = 0;

        try
        {
            TuiSessionContext.Config = new DotsyConfig
            {
                Model = new ModelConfig { Provider = provider }
            };
            TuiSessionContext.ModelListLookup = _ =>
            {
                calls++;
                return Task.FromResult<IReadOnlyList<ModelInfo>>(
                [
                    new("alpha", 1000, 100),
                    new("beta", 1000, 100),
                ]);
            };

            var command = new ModelCommand();
            var first = command.Complete(new CapturingHost(), "");
            var second = command.Complete(new CapturingHost(), "");

            CollectionAssert.AreEqual(
                new[] { "/model alpha", "/model beta" },
                first.Select(i => i.Replacement).ToArray());
            CollectionAssert.AreEqual(
                new[] { "/model alpha", "/model beta" },
                second.Select(i => i.Replacement).ToArray());
            Assert.AreEqual(1, calls);
        }
        finally
        {
            TuiSessionContext.Config = previousConfig;
            TuiSessionContext.ModelListLookup = previousLookup;
        }
    }

    [TestMethod]
    public async Task Complete_ShowsLoadingThenRefreshesWhenModelsArrive()
    {
        var provider = ProviderConfig.Ollama;
        var previousConfig = TuiSessionContext.Config;
        var previousLookup = TuiSessionContext.ModelListLookup;
        var models = new TaskCompletionSource<IReadOnlyList<ModelInfo>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            TuiSessionContext.Config = new DotsyConfig
            {
                Model = new ModelConfig { Provider = provider }
            };
            TuiSessionContext.ModelListLookup = _ => models.Task;

            var host = new CapturingHost();
            var command = new ModelCommand();

            var loading = command.Complete(host, "");
            Assert.AreEqual("loading...", loading.Single().Display);

            models.SetResult(
            [
                new("alpha", 1000, 100),
                new("beta", 1000, 100),
            ]);

            await host.Refreshed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var loaded = command.Complete(host, "");

            CollectionAssert.AreEqual(
                new[] { "/model alpha", "/model beta" },
                loaded.Select(i => i.Replacement).ToArray());
        }
        finally
        {
            TuiSessionContext.Config = previousConfig;
            TuiSessionContext.ModelListLookup = previousLookup;
        }
    }

    private sealed class CapturingHost : ISlashCommandHost
    {
        private readonly StringBuilder output = new();
        public TaskCompletionSource Refreshed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

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
        public void RefreshCompletions() => Refreshed.TrySetResult();
        public CancellationToken BeginScenario() => CancellationToken.None;
        public void EndScenario() { }
        public void RequestStop() { }
        public void Invoke(Action action) => action();
    }
}
