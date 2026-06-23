using Dotsy.Cli.SlashCommands;
using Dotsy.Core.Config;
using Dotsy.Core.Loop;
using Dotsy.Core.Loop.Data;
using Dotsy.Core.Tools;

namespace Dotsy.Core.Tests;

[TestClass]
public sealed class SelfContextBuilderTests
{
    [TestMethod]
    public async Task BuildMarkdown_UsesMarkdownAndIncludesCommandAndToolCatalogues()
    {
        var config = new DotsyConfig();
        var ctx = new LoopContext("session-123");
        var registry = new ToolRegistry();
        registry.Register(new DoneTool());

        var markdown = await new SelfCommand().BuildMarkdownAsync(new SelfCommand.SelfContextRequest(
            config,
            ctx,
            Directory.GetCurrentDirectory(),
            registry,
            SlashCommandRegistry.CreateDefault().Usages,
            GeneratedAt: DateTimeOffset.Parse("2026-06-07T18:42:10-07:00"),
            ProbeTimeout: TimeSpan.Zero));

        StringAssert.StartsWith(markdown, "# Dotsy Self Context");
        StringAssert.Contains(markdown, "## App");
        StringAssert.Contains(markdown, "| Syntax | Description |");
        StringAssert.Contains(markdown, "| /self |");
        StringAssert.Contains(markdown, "| Done |");
        Assert.IsFalse(markdown.Contains("<dotsy_self_context>"));
    }

    [TestMethod]
    public async Task BuildMarkdown_IncludesConfigFilesSectionAndProjectKeySource()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"dotsy_self_{Guid.NewGuid():N}");
        var projectConfig = Path.Combine(tmp, ".dotsy", "config.toml");
        Directory.CreateDirectory(Path.Combine(tmp, ".dotsy"));
        try
        {
            await File.WriteAllTextAsync(projectConfig, """
                [agent]
                max_turns = 42
                """);

            var markdown = await new SelfCommand().BuildMarkdownAsync(new SelfCommand.SelfContextRequest(
                ConfigLoader.Load(tmp),
                new LoopContext("session-123"),
                tmp,
                ProbeTimeout: TimeSpan.Zero));

            StringAssert.Contains(markdown, "## Config Files");
            StringAssert.Contains(markdown, "Global (system)");
            StringAssert.Contains(markdown, ConfigLoader.GlobalConfigPath.Replace("|", "\\|"));
            // The project-set key's Source column points at the project config file.
            StringAssert.Contains(markdown, $"| agent.max_turns | int | 42 | {projectConfig.Replace("|", "\\|")} |");
        }
        finally
        {
            if (Directory.Exists(tmp))
                Directory.Delete(tmp, recursive: true);
        }
    }

    [TestMethod]
    public async Task BuildMarkdown_RedactsSecretValuesWithoutLeakingRawValue()
    {
        var config = new DotsyConfig();
        config.Model.Anthropic.ApiKey = "real-secret-value";
        config.Model.OpenAi.ApiKey = "sk-example-placeholder";

        var markdown = await new SelfCommand().BuildMarkdownAsync(new SelfCommand.SelfContextRequest(
            config,
            new LoopContext("session-123"),
            Directory.GetCurrentDirectory(),
            ProbeTimeout: TimeSpan.Zero));

        StringAssert.Contains(markdown, "| model.anthropic.api_key | string | set:redacted |");
        StringAssert.Contains(markdown, "| model.openai.api_key | string | placeholder |");
        Assert.IsFalse(markdown.Contains("real-secret-value"));
        Assert.IsFalse(markdown.Contains("sk-example-placeholder"));
    }

    [TestMethod]
    public async Task BuildMarkdown_ReportsConfiguredAndResolvedTheme()
    {
        var config = new DotsyConfig();
        config.Tui.Theme = "system";

        var markdown = await new SelfCommand().BuildMarkdownAsync(new SelfCommand.SelfContextRequest(
            config,
            new LoopContext("session-123"),
            Directory.GetCurrentDirectory(),
            ResolvedTheme: "dark",
            ProbeTimeout: TimeSpan.Zero));

        StringAssert.Contains(markdown, "| Theme (configured) | system |");
        StringAssert.Contains(markdown, "| Theme (resolved) | dark |");
    }

    [TestMethod]
    public async Task BuildMarkdown_IncludesContextWindowSection()
    {
        var ctx = new LoopContext("session-123")
        {
            TokenBudget = new TokenBudget(
                ContextWindow: 1_000_000,
                ReserveTokens: 16_384,
                KeepRecentTokens: 20_000,
                UsedTokens: 250_000),
        };

        var markdown = await new SelfCommand().BuildMarkdownAsync(new SelfCommand.SelfContextRequest(
            new DotsyConfig(),
            ctx,
            Directory.GetCurrentDirectory(),
            ProbeTimeout: TimeSpan.Zero));

        StringAssert.Contains(markdown, "## Context Window");
        StringAssert.Contains(markdown, "| Context window (tokens) | 1,000,000 |");
        StringAssert.Contains(markdown, "| Used tokens | 250,000 |");
        StringAssert.Contains(markdown, "| Usage | 25.0% |");
    }

    [TestMethod]
    public async Task BuildMarkdown_ProbeTimeoutFallsBackToUnknown()
    {
        var markdown = await new SelfCommand().BuildMarkdownAsync(new SelfCommand.SelfContextRequest(
            new DotsyConfig(),
            new LoopContext("session-123"),
            Directory.GetCurrentDirectory(),
            ProbeTimeout: TimeSpan.Zero));

        StringAssert.Contains(markdown, "| Platform | unknown |");
        StringAssert.Contains(markdown, "| Present | unknown |");
    }

    [TestMethod]
    public async Task BuildMarkdown_AppliesPromptSizeCap()
    {
        var markdown = await new SelfCommand().BuildMarkdownAsync(new SelfCommand.SelfContextRequest(
            new DotsyConfig(),
            new LoopContext("session-123"),
            Directory.GetCurrentDirectory(),
            MaxChars: 600,
            ProbeTimeout: TimeSpan.Zero));

        Assert.IsTrue(markdown.Length <= 600);
        StringAssert.Contains(markdown, "[Self-context truncated to fit prompt-size cap.]");
    }

    [TestMethod]
    public async Task BuildPrompt_PreservesQuestionTextAndUsesDefaultQuestionWhenEmpty()
    {
        var builder = new SelfCommand();
        var request = new SelfCommand.SelfContextRequest(
            new DotsyConfig(),
            new LoopContext("session-123"),
            Directory.GetCurrentDirectory(),
            ProbeTimeout: TimeSpan.Zero);

        var custom = await builder.BuildPromptAsync(request, "how do I switch models?");
        StringAssert.Contains(custom, "## User Question");
        StringAssert.Contains(custom, "how do I switch models?");

        var defaultPrompt = await builder.BuildPromptAsync(request, "");
        StringAssert.Contains(defaultPrompt, "Summarize the current Dotsy runtime and notable configuration concisely.");
    }
}
