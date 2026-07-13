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

        // Find most recent summary; the model context starts after it (everything before is folded
        // into the summary), but the conversation panel replays the whole transcript.
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
        var displayMessages = BuildMessages(records, 0, ref sessionId, ref cwd);
        var contextMessages = BuildMessages(records, startIdx, ref sessionId, ref cwd);

        return new LoadedSession
        {
            SessionId = sessionId ?? "",
            Messages = contextMessages,
            DisplayMessages = displayMessages,
            ToolInfo = BuildToolInfo(records),
            CompactionSummary = summaryContent,
            Cwd = cwd,
            UsedTokens = ReadLastUsedTokens(records)
        };
    }

    // Reconstructs the message list from records[startIdx..], mirroring how the live loop stores a
    // turn: user/assistant records become their message, and tool_result records become a UserMessage
    // (that is the role tool results are sent back under).
    private static List<Message> BuildMessages(List<JsonElement> records, int startIdx, ref string? sessionId, ref string? cwd)
    {
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

                messages.Add(type == "user" ? new UserMessage(blocks) : new AssistantMessage(blocks));
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
        return messages;
    }

    // Recovers per-tool timing: a tool_use's start time is its assistant record's timestamp, and its
    // run duration is the duration_ms on the matching tool_result content block.
    private static Dictionary<string, RestoredToolInfo> BuildToolInfo(List<JsonElement> records)
    {
        var startedAt = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var durationMs = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var rec in records)
        {
            DateTimeOffset? ts =
                rec.TryGetProperty("timestamp", out var tsEl) &&
                tsEl.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(tsEl.GetString(), out var parsed)
                    ? parsed
                    : null;

            if (!rec.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object ||
                !msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var item in content.EnumerateArray())
            {
                var itemType = item.TryGetProperty("type", out var it) ? it.GetString() : null;
                if (itemType == "tool_use" && ts is { } started && item.TryGetProperty("id", out var idEl))
                    startedAt[idEl.GetString() ?? ""] = started;
                else if (itemType == "tool_result" && item.TryGetProperty("tool_use_id", out var tuid))
                {
                    var id = tuid.GetString() ?? "";
                    if (item.TryGetProperty("duration_ms", out var dEl) && dEl.TryGetInt32(out var d))
                        durationMs[id] = d;
                }
            }
        }

        var info = new Dictionary<string, RestoredToolInfo>(StringComparer.Ordinal);
        foreach (var id in startedAt.Keys.Union(durationMs.Keys))
            info[id] = new RestoredToolInfo(
                startedAt.TryGetValue(id, out var s) ? s : null,
                durationMs.TryGetValue(id, out var dm) ? dm : 0);
        return info;
    }

    // The live token budget tracks input + output of the most recent assistant turn (see AgentLoop's
    // UsageUpdate handling). Scan from the end for the last record carrying a usage object and mirror
    // that sum so a resumed session restores the same context-usage figure it last displayed.
    private static int ReadLastUsedTokens(List<JsonElement> records)
    {
        for (int i = records.Count - 1; i >= 0; i--)
        {
            if (!records[i].TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
                continue;

            int input = usage.TryGetProperty("inputTokens", out var it) && it.TryGetInt32(out var iv) ? iv : 0;
            int output = usage.TryGetProperty("outputTokens", out var ot) && ot.TryGetInt32(out var ov) ? ov : 0;
            return input + output;
        }

        return 0;
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
