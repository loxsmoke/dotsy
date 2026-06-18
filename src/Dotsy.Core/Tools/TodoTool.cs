using System.Text;
using System.Text.Json;

using Dotsy.Core.Utils;
using Dotsy.Core.Tools.Interfaces;

namespace Dotsy.Core.Tools;

public sealed class TodoTool : ITool
{
    public const string ToolName = "Todo";
    public string Name => ToolName;
    public string Description => "Create or replace the structured task list for this session.";
    public JsonElement InputSchema => ToolSchemas.TodoSchema;
    public ToolSafety Safety => ToolSafety.ReadOnly;
    public bool IsCompletionSignal => false;

    public string FormatPanelArgument(JsonElement input, string cwd)
    {
        var items = GetItems(input);
        if (items.Count == 0)
            return "0 tasks";
        if (items.Count == 1)
            return items[0];

        var preview = string.Join(", ", items.Take(3));
        var suffix = items.Count > 3 ? "..." : "";
        return $"Todo  {items.Count} tasks. {preview}{suffix}";
    }

    public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        if (!input.TryGetProperty("items", out var itemsEl))
            return Task.FromResult(ToolResult.Err("items array is required"));

        var items = GetItems(input);

        ctx.LoopContext.TodoItems.Clear();
        ctx.LoopContext.TodoItems.AddRange(items);

        var sb = new StringBuilder();
        sb.AppendLine("Todo updated:");
        for (int i = 0; i < items.Count; i++)
            sb.AppendLine($"  {i + 1}. {items[i]}");

        return Task.FromResult(ToolResult.Ok(sb.ToString().TrimEnd()));
    }

    private static List<string> GetItems(JsonElement input)
    {
        if (!input.TryGetProperty("items", out var itemsEl) || itemsEl.ValueKind != JsonValueKind.Array)
            return [];

        var items = new List<string>();
        foreach (var item in itemsEl.EnumerateArray())
        {
            var text = item.ValueKind == JsonValueKind.String
                ? item.GetString() ?? ""
                : item.GetStringPropertyOrEmpty("description");
            if (!string.IsNullOrWhiteSpace(text))
                items.Add(text);
        }
        return items;
    }
}
