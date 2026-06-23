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

    private static JsonElement Json(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();
}
