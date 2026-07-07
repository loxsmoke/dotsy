using System.Text.Json;
using Dotsy.Core.Loop.Data;
using Dotsy.Core.Providers;
using Dotsy.Core.Tools;

namespace Dotsy.Core.Loop;

public static class ToolPairSummarizer
{
    private const int DefaultKeepRecentMessages = 4;
    private const int MaxResultChars = 160;

    public static Task SummarizeOldPairsInBackground(
        LoopContext ctx, int keepRecentMessages = DefaultKeepRecentMessages, bool preserveLatestReads = true)
    {
        return Task.Run(() => SummarizeOldPairs(ctx, keepRecentMessages, preserveLatestReads));
    }

    public static int SummarizeOldPairs(
        LoopContext ctx, int keepRecentMessages = DefaultKeepRecentMessages, bool preserveLatestReads = true)
    {
        lock (ctx.Messages)
        {
            // Tool-result ids that hold the most recent read of a file; their pair is left verbatim
            // so the model keeps the freshest copy of each file it has read.
            var pinned = preserveLatestReads
                ? LatestReadResultIds(ctx.Messages)
                : new HashSet<string>(StringComparer.Ordinal);
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

                // Preserve the pair holding the latest read of a file (keeps its content live).
                if (toolResults.Any(r => pinned.Contains(r.ToolUseId)))
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

    // For each distinct file path read this session, the tool-result id of its most recent read.
    // Only reads still present as ToolUseBlocks are considered (already-summarized ones are gone),
    // so this naturally tracks the latest live read of each file.
    private static HashSet<string> LatestReadResultIds(IReadOnlyList<Message> messages)
    {
        var latestByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var message in messages)
        {
            if (message is not AssistantMessage assistant)
                continue;
            foreach (var block in assistant.Content)
            {
                if (block is not ToolUseBlock tu || !string.Equals(tu.Name, ReadTool.ToolName, StringComparison.Ordinal))
                    continue;
                if (TryGetPath(tu.Input) is { } path)
                    latestByPath[path] = tu.Id;   // later reads overwrite earlier ones
            }
        }
        return new HashSet<string>(latestByPath.Values, StringComparer.Ordinal);
    }

    private static string? TryGetPath(JsonElement input)
    {
        try
        {
            return input.ValueKind == JsonValueKind.Object && input.TryGetProperty("path", out var p)
                ? p.GetString()
                : null;
        }
        catch { return null; }
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
