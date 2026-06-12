using Dotsy.Core.Providers;

namespace Dotsy.Core.Loop;

public static class ToolPairSummarizer
{
    private const int DefaultKeepRecentMessages = 4;
    private const int MaxResultChars = 160;

    public static Task SummarizeOldPairsInBackground(LoopContext ctx, int keepRecentMessages = DefaultKeepRecentMessages)
    {
        return Task.Run(() => SummarizeOldPairs(ctx, keepRecentMessages));
    }

    public static int SummarizeOldPairs(LoopContext ctx, int keepRecentMessages = DefaultKeepRecentMessages)
    {
        lock (ctx.Messages)
        {
            var cutoff = Math.Max(0, ctx.Messages.Count - keepRecentMessages);
            var summarized = 0;

            for (var i = 0; i < cutoff - 1; i++)
            {
                if (ctx.Messages[i] is not AssistantMessage assistant
                    || ctx.Messages[i + 1] is not UserMessage user)
                    continue;

                var toolUses = assistant.Content.OfType<ToolUseBlock>().ToList();
                var toolResults = user.Content.OfType<ToolResultBlock>().ToList();
                if (toolUses.Count == 0 || toolResults.Count == 0)
                    continue;

                var resultById = toolResults.ToDictionary(r => r.ToolUseId, StringComparer.Ordinal);
                var parts = new List<string>();
                foreach (var toolUse in toolUses)
                {
                    if (!resultById.TryGetValue(toolUse.Id, out var result))
                        continue;

                    var outcome = result.IsError ? "failed" : "returned";
                    parts.Add($"{toolUse.Name} {outcome}: {OneLine(result.Content)}");
                }

                if (parts.Count == 0)
                    continue;

                ctx.Messages[i] = new UserMessage(
                    [new TextBlock($"[tool summary] {string.Join("; ", parts)}.")]);
                ctx.Messages.RemoveAt(i + 1);
                cutoff--;
                summarized++;
            }

            return summarized;
        }
    }

    private static string OneLine(string content)
    {
        var text = string.Join(" ", content
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0));

        if (text.Length == 0)
            return "(no output)";

        return text.Length <= MaxResultChars ? text : text[..MaxResultChars] + "...";
    }
}
