using System.Text.Json;
using Dotsy.Core.Providers;
using Dotsy.Core.Session;

namespace Dotsy.Core.Tests;

[TestClass]
public sealed class SessionLoaderTests
{
    [TestMethod]
    public void Load_PreservesSummaryAndStructuredMessagesAfterCompaction()
    {
        var sessionDir = Path.Combine(Path.GetTempPath(), "dotsy-session-loader-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(sessionDir);
        var path = Path.Combine(sessionDir, "session-1.jsonl");

        File.WriteAllLines(path,
        [
            JsonSerializer.Serialize(new
            {
                sessionId = "session-1",
                type = "user",
                cwd = "C:\\work",
                message = new { content = "before summary" }
            }),
            JsonSerializer.Serialize(new
            {
                sessionId = "session-1",
                type = "summary",
                cwd = "C:\\work",
                message = "summarized context"
            }),
            JsonSerializer.Serialize(new
            {
                sessionId = "session-1",
                type = "assistant",
                cwd = "C:\\work",
                message = new
                {
                    content = new object[]
                    {
                        new { type = "text", text = "checking" },
                        new { type = "tool_use", id = "call-1", name = "Read", input = "{\"path\":\"file.txt\"}" }
                    }
                }
            }),
            JsonSerializer.Serialize(new
            {
                sessionId = "session-1",
                type = "tool_result",
                cwd = "C:\\work",
                message = new
                {
                    content = new object[]
                    {
                        new
                        {
                            type = "tool_result",
                            tool_use_id = "call-1",
                            name = "Read",
                            content = "file contents",
                            is_error = false
                        }
                    }
                }
            })
        ]);

        var loaded = SessionLoader.Load("session-1", sessionDir);

        Assert.IsNotNull(loaded);
        Assert.AreEqual("summarized context", loaded.CompactionSummary);
        Assert.AreEqual(2, loaded.Messages.Count);

        var assistant = (AssistantMessage)loaded.Messages[0];
        Assert.IsInstanceOfType<TextBlock>(assistant.Content[0]);
        Assert.IsInstanceOfType<ToolUseBlock>(assistant.Content[1]);
        var toolUse = (ToolUseBlock)assistant.Content[1];
        Assert.AreEqual("Read", toolUse.Name);
        Assert.AreEqual("file.txt", toolUse.Input.GetProperty("path").GetString());

        var toolResults = (UserMessage)loaded.Messages[1];
        Assert.IsInstanceOfType<ToolResultBlock>(toolResults.Content[0]);
        Assert.AreEqual("file contents", ((ToolResultBlock)toolResults.Content[0]).Content);
    }
}
