using System.Text.Json;
using Dotsy.Core.Loop;
using Dotsy.Core.Loop.Data;
using Dotsy.Core.Providers;

namespace Dotsy.Core.Tests;

[TestClass]
public sealed class ToolPairSummarizerTests
{
    [TestMethod]
    public void SummarizeOldPairs_ReplacesToolUseAndResultWithTextSummary()
    {
        var ctx = new LoopContext();
        ctx.Messages.Add(new UserMessage([new TextBlock("start")]));
        ctx.Messages.Add(new AssistantMessage([new ToolUseBlock("call-1", "Shell", Json("{}"))]));
        ctx.Messages.Add(new UserMessage([new ToolResultBlock("call-1", "line one\nline two")]));
        ctx.Messages.Add(new UserMessage([new TextBlock("recent")]));

        var count = ToolPairSummarizer.SummarizeOldPairs(ctx, keepRecentMessages: 1);

        Assert.AreEqual(1, count);
        Assert.AreEqual(3, ctx.Messages.Count);
        Assert.IsInstanceOfType<UserMessage>(ctx.Messages[1]);
        var summary = ((UserMessage)ctx.Messages[1]).Content.OfType<TextBlock>().Single().Text;
        StringAssert.Contains(summary, "[tool summary]");
        StringAssert.Contains(summary, "Shell returned: line one line two");
    }

    [TestMethod]
    public void SummarizeOldPairs_KeepsRecentToolPairsVerbatim()
    {
        var ctx = new LoopContext();
        ctx.Messages.Add(new AssistantMessage([new ToolUseBlock("call-1", "Read", Json("{}"))]));
        ctx.Messages.Add(new UserMessage([new ToolResultBlock("call-1", "file contents")]));

        var count = ToolPairSummarizer.SummarizeOldPairs(ctx, keepRecentMessages: 2);

        Assert.AreEqual(0, count);
        Assert.IsInstanceOfType<AssistantMessage>(ctx.Messages[0]);
        Assert.IsInstanceOfType<ToolUseBlock>(((AssistantMessage)ctx.Messages[0]).Content[0]);
    }

    [TestMethod]
    public void SummarizeOldPairs_PreservesLatestReadOfEachFile()
    {
        var ctx = new LoopContext();
        ctx.Messages.Add(new AssistantMessage([new ToolUseBlock("r1", "Read", Json("""{"path":"foo.cs"}"""))]));
        ctx.Messages.Add(new UserMessage([new ToolResultBlock("r1", "OLD foo content")]));
        ctx.Messages.Add(new AssistantMessage([new ToolUseBlock("s1", "Shell", Json("{}"))]));
        ctx.Messages.Add(new UserMessage([new ToolResultBlock("s1", "build output")]));
        ctx.Messages.Add(new AssistantMessage([new ToolUseBlock("r2", "Read", Json("""{"path":"foo.cs"}"""))]));
        ctx.Messages.Add(new UserMessage([new ToolResultBlock("r2", "NEW foo content")]));
        ctx.Messages.Add(new UserMessage([new TextBlock("recent")]));

        var count = ToolPairSummarizer.SummarizeOldPairs(ctx, keepRecentMessages: 1);

        var results = ctx.Messages.OfType<UserMessage>()
            .SelectMany(m => m.Content.OfType<ToolResultBlock>()).Select(t => t.Content).ToList();
        Assert.IsTrue(results.Contains("NEW foo content"), "latest read of foo.cs is preserved verbatim");
        Assert.IsFalse(results.Any(c => c.Contains("OLD foo content")), "the superseded older read is summarized");
        Assert.AreEqual(2, count, "the older read pair and the shell pair were summarized, the latest read kept");
    }

    [TestMethod]
    public void SummarizeOldPairs_PreserveDisabled_SummarizesLatestRead()
    {
        var ctx = new LoopContext();
        ctx.Messages.Add(new AssistantMessage([new ToolUseBlock("r1", "Read", Json("""{"path":"foo.cs"}"""))]));
        ctx.Messages.Add(new UserMessage([new ToolResultBlock("r1", "foo content")]));
        ctx.Messages.Add(new UserMessage([new TextBlock("recent")]));

        var count = ToolPairSummarizer.SummarizeOldPairs(ctx, keepRecentMessages: 1, preserveLatestReads: false);

        Assert.AreEqual(1, count);
        var results = ctx.Messages.OfType<UserMessage>().SelectMany(m => m.Content.OfType<ToolResultBlock>()).ToList();
        Assert.AreEqual(0, results.Count, "with preservation off, even the latest read is summarized away");
    }

    private static JsonElement Json(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();
}
