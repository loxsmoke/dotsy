using System.Text.Json;
using Dotsy.Core.Providers;
using Dotsy.Core.Session;
using Dotsy.Core.Session.Data;

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

        // DisplayMessages replays the WHOLE transcript, including the user prompt from before the
        // summary that Messages (the compacted model context) drops.
        Assert.AreEqual(3, loaded.DisplayMessages.Count);
        var firstUser = (UserMessage)loaded.DisplayMessages[0];
        Assert.AreEqual("before summary", ((TextBlock)firstUser.Content[0]).Text);
    }

    [TestMethod]
    public void Load_RecoversToolTimingForRestoredRows()
    {
        var loaded = LoadRecords(
            new
            {
                sessionId = "s3",
                type = "assistant",
                timestamp = "2026-07-08T10:00:00.0000000+00:00",
                message = new
                {
                    content = new object[]
                    {
                        new { type = "tool_use", id = "call-9", name = "Read", input = "{\"path\":\"a.txt\"}" }
                    }
                }
            },
            new
            {
                sessionId = "s3",
                type = "tool_result",
                timestamp = "2026-07-08T10:00:03.0000000+00:00",
                message = new
                {
                    content = new object[]
                    {
                        new { type = "tool_result", tool_use_id = "call-9", name = "Read", content = "ok", is_error = false, duration_ms = 3200 }
                    }
                }
            });

        Assert.IsTrue(loaded.ToolInfo.TryGetValue("call-9", out var info));
        Assert.AreEqual(3200, info!.DurationMs);
        Assert.IsNotNull(info.StartedAt);
        Assert.AreEqual(new DateTimeOffset(2026, 7, 8, 10, 0, 0, TimeSpan.Zero), info.StartedAt!.Value);
    }

    // Forward compatibility: a newer writer adds extra record/block fields and emits record and
    // content-block types this version doesn't know. The loader must ignore the extras and skip the
    // unknown shapes without failing, still recovering the messages it does understand.
    [TestMethod]
    public void Load_IgnoresUnknownExtraFieldsAndRecordTypes()
    {
        var loaded = LoadRecords(
            // Real sessions carry many top-level fields this version never reads.
            new
            {
                sessionId = "s",
                type = "user",
                cwd = "C:\\work",
                uuid = "u-1",
                parentUuid = "u-0",
                timestamp = "2026-06-22T00:00:00Z",
                version = "9.9.9",
                usage = new { inputTokens = 10, outputTokens = 5, cacheReadTokens = 0 },
                message = new { content = "hello", role = "user", extraField = "ignored" }
            },
            new
            {
                sessionId = "s",
                type = "assistant",
                futureTopLevelField = "whatever",
                message = new
                {
                    content = new object[]
                    {
                        new { type = "text", text = "looking", signature = "future-only" },
                        // Unknown content-block type from a newer version → skipped, not an error.
                        new { type = "thinking", thinking = "internal reasoning" },
                        // tool_use with an unknown extra block field and object-form input.
                        new { type = "tool_use", id = "t1", name = "Grep", input = new { pattern = "foo" }, cache_control = new { type = "ephemeral" } }
                    }
                }
            },
            // Entirely unknown record type from a newer version → skipped without crashing.
            new { sessionId = "s", type = "future_event", message = new { content = "ignore me" } },
            new
            {
                sessionId = "s",
                type = "tool_result",
                message = new
                {
                    content = new object[]
                    {
                        new { type = "tool_result", tool_use_id = "t1", content = "match found", is_error = false, name = "Grep", duration_ms = 42 }
                    }
                }
            });

        Assert.AreEqual("s", loaded.SessionId);
        Assert.AreEqual("C:\\work", loaded.Cwd);

        // user(hello) + assistant(text+tool_use, thinking dropped) + tool_result; future_event dropped.
        Assert.AreEqual(3, loaded.Messages.Count);

        var user = (UserMessage)loaded.Messages[0];
        Assert.AreEqual("hello", ((TextBlock)user.Content[0]).Text);

        var assistant = (AssistantMessage)loaded.Messages[1];
        Assert.AreEqual(2, assistant.Content.Count, "Unknown 'thinking' block should be dropped");
        Assert.AreEqual("looking", ((TextBlock)assistant.Content[0]).Text);
        var toolUse = (ToolUseBlock)assistant.Content[1];
        Assert.AreEqual("Grep", toolUse.Name);
        Assert.AreEqual("foo", toolUse.Input.GetProperty("pattern").GetString());

        var toolResult = (ToolResultBlock)((UserMessage)loaded.Messages[2]).Content[0];
        Assert.AreEqual("t1", toolResult.ToolUseId);
        Assert.AreEqual("match found", toolResult.Content);
        Assert.IsFalse(toolResult.IsError);
    }

    // Backward/robustness: an older or partial writer omits fields this version reads. Missing
    // records/messages are skipped, and missing block fields fall back to safe defaults rather than
    // throwing.
    [TestMethod]
    public void Load_ToleratesMissingRequiredFields()
    {
        var loaded = LoadRecords(
            // No 'type': not turned into a message, but sessionId is still harvested from it.
            new { sessionId = "s2", message = new { content = "no type" } },
            // No 'message' at all → skipped.
            new { type = "user" },
            // Message object without 'content' → skipped.
            new { type = "user", message = new { role = "user" } },
            // tool_use block missing id/name/input → empty strings and an empty-object input.
            new
            {
                type = "assistant",
                message = new { content = new object[] { new { type = "tool_use" } } }
            },
            // tool_result block missing tool_use_id/content/is_error → defaults.
            new
            {
                type = "tool_result",
                message = new { content = new object[] { new { type = "tool_result" } } }
            },
            // text block missing 'text' → empty string.
            new
            {
                type = "user",
                message = new { content = new object[] { new { type = "text" } } }
            });

        Assert.AreEqual("s2", loaded.SessionId, "sessionId is read even from a record that isn't a message");
        Assert.IsNull(loaded.Cwd, "no record carried cwd");

        // assistant(tool_use) + tool_result + user(text); the three missing/incomplete records drop out.
        Assert.AreEqual(3, loaded.Messages.Count);

        var toolUse = (ToolUseBlock)((AssistantMessage)loaded.Messages[0]).Content[0];
        Assert.AreEqual("", toolUse.Id);
        Assert.AreEqual("", toolUse.Name);
        Assert.AreEqual(JsonValueKind.Object, toolUse.Input.ValueKind);
        Assert.AreEqual(0, toolUse.Input.EnumerateObject().Count(), "missing input defaults to {}");

        var toolResult = (ToolResultBlock)((UserMessage)loaded.Messages[1]).Content[0];
        Assert.AreEqual("", toolResult.ToolUseId);
        Assert.AreEqual("", toolResult.Content);
        Assert.IsFalse(toolResult.IsError);

        var text = (TextBlock)((UserMessage)loaded.Messages[2]).Content[0];
        Assert.AreEqual("", text.Text);
    }

    // Serializes each record (with its exact key casing) to one JSONL line, then loads it back.
    private static LoadedSession LoadRecords(params object[] records)
    {
        var sessionDir = Path.Combine(Path.GetTempPath(), "dotsy-session-loader-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(sessionDir);
        var path = Path.Combine(sessionDir, "session.jsonl");
        File.WriteAllLines(path, Array.ConvertAll(records, r => JsonSerializer.Serialize(r)));

        var loaded = SessionLoader.Load("session", sessionDir);
        Assert.IsNotNull(loaded);
        return loaded;
    }
}
