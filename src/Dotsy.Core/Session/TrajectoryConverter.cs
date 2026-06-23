using System.Text.Json.Nodes;
using Dotsy.Core.Loop.Data;
using Dotsy.Core.Providers;
using Dotsy.Core.Session.Data;

namespace Dotsy.Core.Session;

public static class TrajectoryConverter
{
    public static List<TrajectoryToolRow> ToToolRows(IReadOnlyList<ToolDefinition> tools) =>
        [.. tools.Select(t => new TrajectoryToolRow
        {
            Id = t.Name,
            Description = t.Description,
            InputSchema = new TrajectoryInputSchema { JsonSchema = t.InputSchema.Clone() }
        })];

    public static List<JsonObject> ToOpenAiMessages(string systemPrompt, LoopContext ctx)
    {
        var messages = new List<JsonObject> { new() { ["role"] = "system", ["content"] = systemPrompt } };

        if (!string.IsNullOrWhiteSpace(ctx.CompactionSummary))
        {
            messages.Add(new JsonObject
            {
                ["role"] = "system",
                ["content"] = $"Provider-facing compaction summary:\n{ctx.CompactionSummary}"
            });
        }

        foreach (var msg in ctx.Messages)
        {
            switch (msg)
            {
                case UserMessage user:
                    AddUserMessages(messages, user);
                    break;
                case AssistantMessage assistant:
                    messages.Add(ToAssistantMessage(assistant));
                    break;
            }
        }

        return messages;
    }

    private static void AddUserMessages(List<JsonObject> messages, UserMessage user)
    {
        var textParts = new List<string>();
        foreach (var block in user.Content)
        {
            if (block is ToolResultBlock tr)
            {
                if (textParts.Count > 0)
                {
                    messages.Add(new JsonObject { ["role"] = "user", ["content"] = string.Join("\n", textParts) });
                    textParts.Clear();
                }

                messages.Add(new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = tr.ToolUseId,
                    ["name"] = "",
                    ["content"] = tr.Content
                });
            }
            else if (block is TextBlock tb)
            {
                textParts.Add(tb.Text);
            }
        }

        if (textParts.Count > 0)
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = string.Join("\n", textParts) });
    }

    private static JsonObject ToAssistantMessage(AssistantMessage assistant)
    {
        var content = string.Join("\n", assistant.Content.OfType<TextBlock>().Select(b => b.Text));
        var obj = new JsonObject { ["role"] = "assistant", ["content"] = content };
        var toolCalls = new JsonArray();

        foreach (var toolUse in assistant.Content.OfType<ToolUseBlock>())
        {
            toolCalls.Add(new JsonObject
            {
                ["id"] = toolUse.Id,
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = toolUse.Name,
                    ["arguments"] = toolUse.Input.GetRawText()
                }
            });
        }

        if (toolCalls.Count > 0)
            obj["tool_calls"] = toolCalls;

        return obj;
    }
}
