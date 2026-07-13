using System.Text.Json;

using Dotsy.Core.Utils;
using Dotsy.Core.Tools.Interfaces;

namespace Dotsy.Core.Tools;

public sealed class TaskTool : ITool
{
    public const string ToolName = "Task";
    public string Name => ToolName;
    public string Description => "Launch a background sub-task agent, or check a launched sub-task by task_id.";
    public JsonElement InputSchema => ToolSchemas.TaskSchema;
    public ToolSafety Safety => ToolSafety.Sequential;
    public bool IsCompletionSignal => false;

    // Populated by the loop engine once AgentLoop is available
    public Func<string, string, CancellationToken, Task<string>>? LaunchSubTask { get; set; }
    public Func<string, CancellationToken, Task<string>>? GetSubTaskStatus { get; set; }

    // Without these overrides the ITool default dumps the raw input JSON — for Task that is the
    // entire multi-paragraph sub-task prompt, which overwhelmed the approval dialog. Show the
    // short description (that's what the field is for), falling back to the prompt's first line.
    public string FormatRunApproval(JsonElement input, string cwd) => Summarize(input);

    public string FormatPanelArgument(JsonElement input, string cwd) => Summarize(input);

    private static string Summarize(JsonElement input)
    {
        if (input.TryGetProperty("task_id", out var taskId)
            && taskId.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(taskId.GetString()))
            return $"check sub-task {taskId.GetString()}";

        var summary = input.GetStringPropertyOrEmpty("description").Trim();
        if (string.IsNullOrWhiteSpace(summary))
            summary = input.GetStringPropertyOrEmpty("prompt");
        return $"\"{Snippet(summary, 80)}\"";
    }

    private static string Snippet(string text, int maxLen)
    {
        var first = text.Split('\n')[0].Trim();
        return first.Length <= maxLen ? first : first[..maxLen] + "...";
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        if (input.TryGetProperty("task_id", out var taskIdEl)
            && !string.IsNullOrWhiteSpace(taskIdEl.GetString()))
        {
            if (GetSubTaskStatus is null)
                return ToolResult.Err("Sub-task status is not yet available");

            return ToolResult.Ok(await GetSubTaskStatus(taskIdEl.GetString()!, ct));
        }

        if (LaunchSubTask is null)
            return ToolResult.Err("Sub-task launching is not yet available");

        var description = input.GetStringPropertyOrEmpty("description");
        if (!input.TryGetProperty("prompt", out var promptEl))
            return ToolResult.Err("Task requires either task_id or prompt");

        var prompt = promptEl.GetString() ?? "";

        var taskId = await LaunchSubTask(description, prompt, ct);
        return ToolResult.Ok($"task_id={taskId}");
    }
}
