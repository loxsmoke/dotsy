using System.Text;
using Dotsy.Cli.SlashCommands;
using Dotsy.Cli.SlashCommands.Interfaces;
using Dotsy.Cli.Tui;
using Dotsy.Core.Config;
using Dotsy.Core.Loop;
using Dotsy.Core.Session.Data;

namespace Dotsy.Cli.Tests;

[TestClass]
public sealed class ConfigCommandTests
{
    private string tmpDir = "";

    [TestInitialize]
    public void Setup()
    {
        tmpDir = Path.Combine(Path.GetTempPath(), $"dotsy_config_command_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(tmpDir))
            Directory.Delete(tmpDir, recursive: true);
    }

    [TestMethod]
    public void Execute_NoArgsShowsGroupedConfigAndActiveProviderSection()
    {
        WithContext(Config(provider: "openai"), projectConfigPath: null, () =>
        {
            var host = new CapturingHost();

            new ConfigCommand().Execute(host, "");

            StringAssert.Contains(host.Output, "[model]");
            StringAssert.Contains(host.Output, "[model.openai]");
            StringAssert.Contains(host.Output, "provider");
            StringAssert.Contains(host.Output, "/config <key> <value>");
        });
    }

    [TestMethod]
    public void Execute_ListShowsAvailableKeys()
    {
        WithContext(Config(), projectConfigPath: null, () =>
        {
            var host = new CapturingHost();

            new ConfigCommand().Execute(host, "list");

            StringAssert.Contains(host.Output, "Available config keys:");
            StringAssert.Contains(host.Output, "model.provider");
            StringAssert.Contains(host.Output, "tui.theme");
        });
    }

    [TestMethod]
    public void Execute_DotlessTextReportsThatItIsNotAConfigCommand()
    {
        WithContext(Config(), projectConfigPath: null, () =>
        {
            var host = new CapturingHost();

            new ConfigCommand().Execute(host, "hello agent");

            StringAssert.Contains(host.Output, "isn't a config command");
            StringAssert.Contains(host.Output, "without the leading /config");
        });
    }

    [TestMethod]
    public void Execute_SetThemePersistsAppliesThemeAndUpdatesModel()
    {
        var projectConfig = WriteProjectConfig();
        var config = Config();

        WithContext(config, projectConfig, () =>
        {
            var host = new CapturingHost { ApplyThemeResult = ("dark", fellBack: false) };

            new ConfigCommand().Execute(host, "tui.theme dark");

            Assert.AreEqual("dark", config.Tui.Theme);
            Assert.AreEqual("dark", host.AppliedTheme);
            Assert.AreEqual(config.Model.ActiveModelId, host.ModelId);
            StringAssert.Contains(host.Output, "tui.theme ");
            StringAssert.Contains(host.Output, "saved");
            StringAssert.Contains(File.ReadAllText(projectConfig), "theme = \"dark\"");
        });
    }

    [TestMethod]
    public void Execute_SetThemeWarnsWhenThemeFallsBack()
    {
        var projectConfig = WriteProjectConfig();

        WithContext(Config(), projectConfig, () =>
        {
            var host = new CapturingHost { ApplyThemeResult = ("dark", fellBack: true) };

            new ConfigCommand().Execute(host, "tui.theme mystery");

            StringAssert.Contains(host.Output, "unknown theme 'mystery', using dark");
        });
    }

    [TestMethod]
    public void Execute_SetModelWhileBusyDefersProviderReload()
    {
        var projectConfig = WriteProjectConfig();
        var config = Config();
        var factoryCalled = false;

        WithContext(config, projectConfig, () =>
        {
            TuiSessionContext.LoopFactory = () =>
            {
                factoryCalled = true;
                throw new InvalidOperationException("should not reload while busy");
            };
            var host = new CapturingHost { Busy = true };

            new ConfigCommand().Execute(host, "model.provider openai");

            Assert.AreEqual("openai", config.Model.Provider);
            Assert.IsFalse(factoryCalled);
            StringAssert.Contains(host.Output, "provider will reload after the current turn completes");
        });
    }

    [TestMethod]
    public void Execute_InvalidConfigKeyWritesError()
    {
        var projectConfig = WriteProjectConfig();

        WithContext(Config(), projectConfig, () =>
        {
            var host = new CapturingHost();

            new ConfigCommand().Execute(host, "model.unknown.id nope");

            StringAssert.Contains(host.Output, "config error: unknown sub-section 'model.unknown'");
        });
    }

    private string WriteProjectConfig()
    {
        var path = Path.Combine(tmpDir, "config.toml");
        File.WriteAllText(path, "[tui]\ntheme = \"dark\"\n\n[model]\nprovider = \"anthropic\"\n");
        return path;
    }

    private static DotsyConfig Config(string provider = "anthropic") => new()
    {
        Model = new ModelConfig
        {
            Provider = provider,
            Anthropic = new AnthropicConfig { Id = "claude" },
            OpenAi = new OpenAiConfig { Id = "gpt", BaseUrl = "https://openai.example.test" }
        },
        Tui = new TuiConfig { Theme = "dark" }
    };

    private static void WithContext(DotsyConfig config, string? projectConfigPath, Action action)
    {
        var previousConfig = TuiSessionContext.Config;
        var previousProjectConfigPath = TuiSessionContext.ProjectConfigPath;
        var previousLoopFactory = TuiSessionContext.LoopFactory;
        var previousLoop = TuiSessionContext.Loop;
        try
        {
            TuiSessionContext.Config = config;
            TuiSessionContext.ProjectConfigPath = projectConfigPath;
            TuiSessionContext.LoopFactory = null;
            TuiSessionContext.Loop = null;
            action();
        }
        finally
        {
            TuiSessionContext.Config = previousConfig;
            TuiSessionContext.ProjectConfigPath = previousProjectConfigPath;
            TuiSessionContext.LoopFactory = previousLoopFactory;
            TuiSessionContext.Loop = previousLoop;
        }
    }

    private sealed class CapturingHost : ISlashCommandHost
    {
        private readonly StringBuilder output = new();

        public string Output => output.ToString();
        public bool Busy { get; init; }
        public string? ModelId { get; private set; }
        public string? AppliedTheme { get; private set; }
        public (string Resolved, bool fellBack) ApplyThemeResult { get; init; } = ("dark", false);

        public IReadOnlyList<SlashCommandUsage> CommandUsages => [];
        public bool IsBusy => Busy;

        public void Write(string text, Terminal.Gui.Drawing.Attribute color) => output.Append(text);
        public void WriteError(string message) => output.Append(message);
        public void WriteDescription(int nameWidth, string name, string description, Terminal.Gui.Drawing.Attribute? nameColor = null) =>
            output.Append(name).Append(' ').Append(description).AppendLine();

        public void SetState(string state) { }
        public void SetModel(string id) => ModelId = id;
        public void SetSession(string id) { }
        public void UpdateStatusBarFromCtx() { }
        public void StartSpinner(string state) { }
        public void StopSpinner(string state) { }
        public void ResetConversationView() { }
        public void ResetToolAndFilePanels() { }
        public void RefreshChangedFiles() { }
        public void RenderLoadedSession(LoadedSession loaded) { }
        public (string Resolved, bool FellBack) ApplyTheme(string value)
        {
            AppliedTheme = value;
            return ApplyThemeResult;
        }
        public void SubmitUserPrompt(string displayText, string promptText) { }
        public void AddPromptHistory(string entry) { }
        public CancellationToken BeginScenario() => CancellationToken.None;
        public void EndScenario() { }
        public void RequestStop() { }
        public void Invoke(Action action) => action();
    }
}
