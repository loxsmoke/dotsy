using Dotsy.Core.Config;
using Dotsy.Core.Loop;
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

        var markdown = await new SelfContextBuilder().BuildMarkdownAsync(new SelfContextRequest(
            config,
            ctx,
            Directory.GetCurrentDirectory(),
            registry,
            SlashCommandCatalog.Commands,
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
    public async Task BuildMarkdown_RedactsSecretValuesWithoutLeakingRawValue()
    {
        var config = new DotsyConfig();
        config.Model.Anthropic.ApiKey = "real-secret-value";
        config.Model.OpenAi.ApiKey = "sk-example-placeholder";

        var markdown = await new SelfContextBuilder().BuildMarkdownAsync(new SelfContextRequest(
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
    public async Task BuildMarkdown_ProbeTimeoutFallsBackToUnknown()
    {
        var markdown = await new SelfContextBuilder().BuildMarkdownAsync(new SelfContextRequest(
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
        var markdown = await new SelfContextBuilder().BuildMarkdownAsync(new SelfContextRequest(
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
        var builder = new SelfContextBuilder();
        var request = new SelfContextRequest(
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
