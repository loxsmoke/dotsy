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
        WithContext(Config(provider: ProviderConfig.OpenAi), projectConfigPath: null, () =>
        {
            var host = new CapturingHost();

            new ConfigCommand().Execute(host, "");

            StringAssert.Contains(host.Output, "[model]");
            StringAssert.Contains(host.Output, $"[{"model." + ProviderConfig.OpenAi}]");
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
            StringAssert.Contains(host.Output, "[model]");
            StringAssert.Contains(host.Output, "provider");
            StringAssert.Contains(host.Output, $"[{"model." + ProviderConfig.OpenAi}]");
            StringAssert.Contains(host.Output, "id");
            Assert.IsFalse(host.Output.Contains("model." + ProviderConfig.OpenAi + ".id", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(host.Output.Contains("tui.theme", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(host.Output.Contains("provider                string", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(host.Output.Contains("verbose                 bool", StringComparison.OrdinalIgnoreCase));

            var lines = host.Output.Split('\n');
            var providerLine = lines.Single(l => l.Contains("active provider"));
            Assert.IsTrue(providerLine.StartsWith(" provider", StringComparison.Ordinal));
            Assert.IsFalse(providerLine.StartsWith("  provider", StringComparison.Ordinal));
            StringAssert.Contains(host.Output, "max_output_tokens_per_request max output tokens per request");
            StringAssert.Contains(host.Output, "left_panel_width_percentage conversation panel width percentage");
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

            new ConfigCommand().Execute(host, "tui.theme light");

            Assert.AreEqual("light", config.Tui.Theme);
            Assert.AreEqual("light", host.AppliedTheme);
            Assert.AreEqual(config.Model.ActiveModel.Id, host.ModelId);
            StringAssert.Contains(host.Output, "tui.theme ");
            StringAssert.Contains(host.Output, "Saved");
            StringAssert.Contains(File.ReadAllText(projectConfig), "theme = \"light\"");
        });
    }

    [TestMethod]
    public void Execute_UnchangedSettingDoesNotPersist()
    {
        var projectConfig = WriteProjectConfig();
        var before = File.ReadAllText(projectConfig);

        WithContext(Config(), projectConfig, () =>
        {
            var host = new CapturingHost();

            new ConfigCommand().Execute(host, $"model.provider {ProviderConfig.Anthropic}");

            Assert.AreEqual(before, File.ReadAllText(projectConfig));
            StringAssert.Contains(host.Output, "model.provider ");
            StringAssert.Contains(host.Output, $"= {ProviderConfig.Anthropic}");
            StringAssert.Contains(host.Output, "Value was unchanged.");
            Assert.IsFalse(host.Output.Contains("saved", StringComparison.OrdinalIgnoreCase));
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

            StringAssert.Contains(host.Output, "Unknown theme 'mystery', using dark");
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

            new ConfigCommand().Execute(host, $"model.provider {ProviderConfig.OpenAi}");

            Assert.AreEqual(ProviderConfig.OpenAi, config.Model.Provider);
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

            StringAssert.Contains(host.Output, "Config error: unknown sub-section 'model.unknown'");
        });
    }

    [TestMethod]
    public void Complete_TopLevelShowsListAndSections()
    {
        var items = new ConfigCommand().Complete(new CapturingHost(), "");

        CollectionAssert.Contains(items.Select(i => i.Replacement).ToList(), "/config list");
        CollectionAssert.Contains(items.Select(i => i.Replacement).ToList(), "/config model ");
        CollectionAssert.Contains(items.Select(i => i.Replacement).ToList(), "/config tui ");
    }

    [TestMethod]
    public void Complete_SectionShowsSectionKeys()
    {
        var items = new ConfigCommand().Complete(new CapturingHost(), "tui ");

        CollectionAssert.Contains(items.Select(i => i.Replacement).ToList(), "/config tui.theme ");
        CollectionAssert.Contains(items.Select(i => i.Replacement).ToList(), "/config tui.verbose ");
    }

    [TestMethod]
    public void Complete_DottedPrefixShowsMatchingKeys()
    {
        var items = new ConfigCommand().Complete(new CapturingHost(), "model.");

        CollectionAssert.Contains(items.Select(i => i.Replacement).ToList(), "/config model.provider ");
        CollectionAssert.Contains(items.Select(i => i.Replacement).ToList(), $"/config {"model." + ProviderConfig.Anthropic + ".id"} ");
    }

    [TestMethod]
    public void Complete_BooleanKeyShowsValuesAfterSpace()
    {
        var items = new ConfigCommand().Complete(new CapturingHost(), "tui.verbose ");

        CollectionAssert.AreEqual(
            new[] { "/config tui.verbose true", "/config tui.verbose false" },
            items.Select(i => i.Replacement).ToArray());
    }

    [TestMethod]
    public void Complete_FiniteValueKeyShowsKnownValuesAfterSpace()
    {
        var items = new ConfigCommand().Complete(new CapturingHost(), "model.provider ");

        CollectionAssert.Contains(items.Select(i => i.Replacement).ToList(), $"/config model.provider {ProviderConfig.Anthropic}");
        CollectionAssert.Contains(items.Select(i => i.Replacement).ToList(), $"/config model.provider {ProviderConfig.OpenAi}");
        CollectionAssert.Contains(items.Select(i => i.Replacement).ToList(), $"/config model.provider {ProviderConfig.Ollama}");
    }

    private string WriteProjectConfig()
    {
        var path = Path.Combine(tmpDir, "config.toml");
        File.WriteAllText(path, $"[tui]\ntheme = \"dark\"\n\n[model]\nprovider = \"{ProviderConfig.Anthropic}\"\n");
        return path;
    }

    private static DotsyConfig Config(string provider = ProviderConfig.Anthropic) => new()
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
