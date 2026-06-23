using System.Text.Json;
using Dotsy.Core.Utils;
using Dotsy.Core.Providers;
using Dotsy.Core.Session.Data;

namespace Dotsy.Core.Session;

public static class SessionLoader
{
    public static LoadedSession? Load(string sessionId, string sessionDir)
    {
        var path = Path.Combine(sessionDir, $"{sessionId}.jsonl");
        return File.Exists(path) ? LoadFromFile(path) : null;
    }

    public static LoadedSession? LoadMostRecent(string sessionDir, string? cwdFilter = null)
    {
        var sessions = SessionStore.GetAllSessions(sessionDir, cwdFilter);
        var latest = sessions.FirstOrDefault();
        return latest is null ? null : Load(latest.SessionId, sessionDir);
    }

    private static LoadedSession LoadFromFile(string path)
    {
        var lines = File.ReadAllLines(path);
        var records = new List<JsonElement>();

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try { records.Add(JsonDocument.Parse(line).RootElement.Clone()); }
            catch { }
        }

        // Find most recent summary; skip messages before it
        string? summaryContent = null;
        int startIdx = 0;
        for (int i = records.Count - 1; i >= 0; i--)
        {
            if (records[i].GetStringPropertyOrEmpty("type") == "summary")
            {
                summaryContent = records[i].TryGetProperty("message", out var m) ? m.GetString() : null;
                startIdx = i + 1;
                break;
            }
        }

        string? sessionId = null;
        string? cwd = null;
        var messages = new List<Message>();

        for (int i = startIdx; i < records.Count; i++)
        {
            var rec = records[i];
            if (sessionId is null && rec.TryGetProperty("sessionId", out _))
                sessionId = rec.GetStringPropertyOrEmpty("sessionId");
            if (cwd is null && rec.TryGetProperty("cwd", out _))
                cwd = rec.GetStringPropertyOrEmpty("cwd");

            var type = rec.TryGetProperty("type", out var tp) ? tp.GetString() : null;
            if ((type == "user" || type == "assistant") &&
                rec.TryGetProperty("message", out var msg) &&
                msg.ValueKind == JsonValueKind.Object &&
                msg.TryGetProperty("content", out var contentEl))
            {
                var blocks = ParseContentBlocks(contentEl);
                if (blocks.Count == 0) continue;

                if (type == "user")
                    messages.Add(new UserMessage(blocks));
                else
                    messages.Add(new AssistantMessage(blocks));
            }
            else if (type == "tool_result" &&
                rec.TryGetProperty("message", out var toolMsg) &&
                toolMsg.ValueKind == JsonValueKind.Object &&
                toolMsg.TryGetProperty("content", out var toolContent))
            {
                var blocks = ParseContentBlocks(toolContent);
                if (blocks.Count > 0)
                    messages.Add(new UserMessage(blocks));
            }
        }

        return new LoadedSession
        {
            SessionId = sessionId ?? "",
            Messages = messages,
            CompactionSummary = summaryContent,
            Cwd = cwd
        };
    }

    private static List<ContentBlock> ParseContentBlocks(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
            return [new TextBlock(content.GetString() ?? "")];

        if (content.ValueKind != JsonValueKind.Array)
            return [];

        var blocks = new List<ContentBlock>();
        foreach (var item in content.EnumerateArray())
        {
            var type = item.TryGetProperty("type", out var typeEl)
                ? typeEl.GetString()
                : null;

            switch (type)
            {
                case "text":
                    blocks.Add(new TextBlock(item.GetStringPropertyOrEmpty("text")));
                    break;

                case "tool_use":
                {
                    var id = item.GetStringPropertyOrEmpty("id");
                    var name = item.GetStringPropertyOrEmpty("name");
                    var input = ParseJsonElement(item.TryGetProperty("input", out var inputEl) ? inputEl : default);
                    blocks.Add(new ToolUseBlock(id, name, input));
                    break;
                }

                case "tool_result":
                {
                    var toolUseId = item.GetStringPropertyOrEmpty("tool_use_id");
                    var contentText = item.TryGetProperty("content", out var contentEl)
                        ? JsonElementToString(contentEl)
                        : "";
                    var isError = item.TryGetProperty("is_error", out var isErrorEl)
                        && isErrorEl.ValueKind == JsonValueKind.True;
                    blocks.Add(new ToolResultBlock(toolUseId, contentText, isError));
                    break;
                }
            }
        }

        return blocks;
    }

    private static JsonElement ParseJsonElement(JsonElement input)
    {
        if (input.ValueKind == JsonValueKind.Undefined || input.ValueKind == JsonValueKind.Null)
            return JsonDocument.Parse("{}").RootElement.Clone();

        if (input.ValueKind == JsonValueKind.String)
        {
            var raw = input.GetString();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                try { return JsonDocument.Parse(raw).RootElement.Clone(); }
                catch { }
            }
            return JsonDocument.Parse("{}").RootElement.Clone();
        }

        return input.Clone();
    }

    private static string JsonElementToString(JsonElement value) =>
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : value.GetRawText();
}
