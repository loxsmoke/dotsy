using Dotsy.Core.Config;
using Dotsy.Core.Loop.Data;
using Dotsy.Core.Providers;

namespace Dotsy.Core.Loop;

public sealed class ContextTooSmallException(string message) : Exception(message);

public static class RequestBuilder
{
    // Rough token estimate: 1 token ≈ 4 chars
    private const int CharsPerToken = 4;

    public static ChatRequest Build(
        DotsyConfig config,
        string systemPrompt,
        LoopContext ctx,
        IReadOnlyList<ToolDefinition> toolDefs)
    {
        var budget = ctx.TokenBudget;
        int usable = budget.Usable;

        // Measure non-negotiable block
        int sysTokens = systemPrompt.Length / CharsPerToken;
        int toolsTokens = EstimateToolsTokens(toolDefs);
        int nonNegotiable = sysTokens + toolsTokens;

        if (nonNegotiable > usable && usable > 0)
            throw new ContextTooSmallException(
                $"System prompt + tools ({nonNegotiable} tokens) exceeds usable context ({usable} tokens)");

        // Flatten messages: merge adjacent same-role messages
        var messages = FlattenMessages(ctx.Messages);

        // Prune oldest messages to fit within budget
        if (usable > 0)
        {
            int remaining = usable - nonNegotiable;
            messages = PruneToFit(messages, remaining);
        }

        return new ChatRequest(
            config.Model.ActiveModelId,
            systemPrompt,
            messages,
            toolDefs,
            config.Model.MaxOutputTokensPerRequest);
    }

    private static List<Message> FlattenMessages(IReadOnlyList<Message> messages)
    {
        var result = new List<Message>();
        foreach (var msg in messages)
        {
            if (result.Count > 0 && result[^1].Role == msg.Role)
            {
                // Merge same-role adjacent messages
                var prev = result[^1];
                IReadOnlyList<ContentBlock> prevBlocks = prev switch
                {
                    UserMessage u => u.Content,
                    AssistantMessage a => a.Content,
                    _ => []
                };
                IReadOnlyList<ContentBlock> curBlocks = msg switch
                {
                    UserMessage u => u.Content,
                    AssistantMessage a => a.Content,
                    _ => []
                };
                var merged = prevBlocks.Concat(curBlocks).ToList();
                result[^1] = msg.Role == "user"
                    ? new UserMessage(merged)
                    : new AssistantMessage(merged);
            }
            else
            {
                result.Add(msg);
            }
        }
        return result;
    }

    private static List<Message> PruneToFit(List<Message> messages, int tokenBudget)
    {
        // Calculate total tokens
        int total = messages.Sum(MessageTokens);
        if (total <= tokenBudget)
            return messages;

        // Remove oldest messages (keeping at least the last 2)
        int start = 0;
        while (total > tokenBudget && start < messages.Count - 2)
        {
            total -= MessageTokens(messages[start]);
            start++;
        }
        return messages.Skip(start).ToList();
    }

    private static int MessageTokens(Message msg)
    {
        IReadOnlyList<ContentBlock> blocks = msg switch
        {
            UserMessage u => u.Content,
            AssistantMessage a => a.Content,
            _ => []
        };
        int chars = blocks.Sum(b => b switch
        {
            TextBlock tb => tb.Text.Length,
            ToolResultBlock tr => tr.Content.Length,
            ToolUseBlock tu => tu.Input.GetRawText().Length,
            _ => 20
        });
        return Math.Max(1, chars / CharsPerToken);
    }

    private static int EstimateToolsTokens(IReadOnlyList<ToolDefinition> tools)
    {
        int chars = tools.Sum(t =>
            t.Name.Length + t.Description.Length + t.InputSchema.GetRawText().Length);
        return chars / CharsPerToken;
    }
}
