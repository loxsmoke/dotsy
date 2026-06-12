using System.Text.Json;

using Dotsy.Core.Utils;
using Dotsy.Core.Tools.Interfaces;

namespace Dotsy.Core.Tools;

public sealed class TaskTool : ITool
{
    public string Name => "Task";
    public string Description => "Launch a background sub-task agent, or check a launched sub-task by task_id.";
    public JsonElement InputSchema => ToolSchemas.TaskSchema;
    public ToolSafety Safety => ToolSafety.Sequential;
    public bool IsCompletionSignal => false;

    // Populated by the loop engine once AgentLoop is available
    public Func<string, string, CancellationToken, Task<string>>? LaunchSubTask { get; set; }
    public Func<string, CancellationToken, Task<string>>? GetSubTaskStatus { get; set; }

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
