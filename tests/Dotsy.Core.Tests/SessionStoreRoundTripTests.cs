using Dotsy.Core.Providers;
using Dotsy.Core.Session;
using Dotsy.Core.Session.Data;

namespace Dotsy.Core.Tests;

[TestClass]
public sealed class SessionStoreRoundTripTests
{
    // Verifies that records written by SessionStore.Append() (the same path used by AgentLoop and
    // the CLI) can be read back by SessionLoader.Load(), which is what the /resume command uses.
    [TestMethod]
    public void Append_ThenLoad_RoundTripsUserAssistantAndToolResultRecords()
    {
        var sessionDir = Path.Combine(Path.GetTempPath(), "dotsy-roundtrip-tests", Guid.NewGuid().ToString());
        const string sessionId = "test-session";
        var store = new SessionStore(sessionId, sessionDir);

        // User turn — matches the shape written by AgentWindow.Prompt.cs and Program.cs
        store.Append(new SessionRecord
        {
            Type = SessionRecordType.User,
            Cwd = @"C:\work",
            Message = new { content = "hello world" }
        });

        // Assistant turn with text + tool_use — matches AgentLoop.cs
        store.Append(new SessionRecord
        {
            Type = SessionRecordType.Assistant,
            Cwd = @"C:\work",
            Message = new
            {
                content = new object[]
                {
                    new { type = "text", text = "I will read the file." },
                    new { type = "tool_use", id = "call-1", name = "Read", input = new { path = "file.txt" } }
                }
            },
            Usage = new SessionUsage { InputTokens = 100, OutputTokens = 20 }
        });

        // Tool-result turn — matches AgentLoop.cs
        store.Append(new SessionRecord
        {
            Type = SessionRecordType.ToolResult,
            Cwd = @"C:\work",
            Message = new
            {
                content = new object[]
                {
                    new { type = "tool_result", tool_use_id = "call-1", name = "Read", content = "file contents", is_error = false }
                }
            }
        });

        var loaded = SessionLoader.Load(sessionId, sessionDir);

        Assert.IsNotNull(loaded);
        Assert.AreEqual(sessionId, loaded.SessionId);
        Assert.AreEqual(@"C:\work", loaded.Cwd);
        Assert.AreEqual(3, loaded.Messages.Count);

        // User message
        var user = (UserMessage)loaded.Messages[0];
        Assert.AreEqual(1, user.Content.Count);
        Assert.AreEqual("hello world", ((TextBlock)user.Content[0]).Text);

        // Assistant message
        var assistant = (AssistantMessage)loaded.Messages[1];
        Assert.AreEqual(2, assistant.Content.Count);
        Assert.AreEqual("I will read the file.", ((TextBlock)assistant.Content[0]).Text);
        var toolUse = (ToolUseBlock)assistant.Content[1];
        Assert.AreEqual("call-1", toolUse.Id);
        Assert.AreEqual("Read", toolUse.Name);
        Assert.AreEqual("file.txt", toolUse.Input.GetProperty("path").GetString());

        // Tool-result message (loader wraps it as a UserMessage)
        var toolResult = (UserMessage)loaded.Messages[2];
        Assert.AreEqual(1, toolResult.Content.Count);
        var tr = (ToolResultBlock)toolResult.Content[0];
        Assert.AreEqual("call-1", tr.ToolUseId);
        Assert.AreEqual("file contents", tr.Content);
        Assert.IsFalse(tr.IsError);
    }

    // Summary records written by SessionStore must be honoured by the loader: messages before the
    // summary are skipped and the summary text is surfaced via CompactionSummary.
    [TestMethod]
    public void Append_ThenLoad_SummaryRecordCausesPreviousMessagesToBeSkipped()
    {
        var sessionDir = Path.Combine(Path.GetTempPath(), "dotsy-roundtrip-tests", Guid.NewGuid().ToString());
        const string sessionId = "test-summary-session";
        var store = new SessionStore(sessionId, sessionDir);

        store.Append(new SessionRecord
        {
            Type = SessionRecordType.User,
            Cwd = @"C:\work",
            Message = new { content = "before summary — should be skipped" }
        });

        // Summary record — matches AgentLoop.cs compaction path
        store.Append(new SessionRecord
        {
            Type = SessionRecordType.Summary,
            Cwd = @"C:\work",
            Message = "summarized context"
        });

        store.Append(new SessionRecord
        {
            Type = SessionRecordType.User,
            Cwd = @"C:\work",
            Message = new { content = "after summary" }
        });

        var loaded = SessionLoader.Load(sessionId, sessionDir);

        Assert.IsNotNull(loaded);
        Assert.AreEqual("summarized context", loaded.CompactionSummary);

        // Only the message after the summary should be present
        Assert.AreEqual(1, loaded.Messages.Count);
        var user = (UserMessage)loaded.Messages[0];
        Assert.AreEqual("after summary", ((TextBlock)user.Content[0]).Text);
    }
}
