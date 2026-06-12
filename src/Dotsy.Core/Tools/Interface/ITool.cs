using System.Text.Json;
using Dotsy.Core.Tools;

namespace Dotsy.Core.Tools.Interfaces
{
    public interface ITool
    {
        /// <summary>
        /// Gets the unique tool name used in model requests and tool-call dispatch.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Gets the human-readable description of what the tool does.
        /// </summary>
        string Description { get; }

        /// <summary>
        /// Gets the JSON schema that describes the tool input accepted by the model.
        /// </summary>
        JsonElement InputSchema { get; }

        /// <summary>
        /// Gets the safety classification used to determine approval and execution behavior.
        /// </summary>
        ToolSafety Safety { get; }

        /// <summary>
        /// Gets a value indicating whether execution of this tool should complete the agent loop.
        /// </summary>
        bool IsCompletionSignal { get; }

        /// <summary>
        /// Gets a value indicating whether this tool writes to the workspace.
        /// </summary>
        bool IsWriteTool => false;

        /// <summary>
        /// Formats tool input for a human approval prompt before the tool runs.
        /// </summary>
        string FormatRunApproval(JsonElement input, string cwd) => input.GetRawText();

        /// <summary>
        /// Formats tool input for the TUI tool panel while the tool is running.
        /// </summary>
        string FormatPanelArgument(JsonElement input, string cwd) => input.GetRawText();

        /// <summary>
        /// Formats tool output for the TUI tool panel after the tool completes.
        /// </summary>
        string? FormatPanelResult(JsonElement input, string resultContent, string cwd) => null;

        Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext ctx, CancellationToken ct);
    }
}

namespace Dotsy.Core.Tools
{
    public enum ToolSafety { ReadOnly, Sequential, Destructive }

    public record ToolResult(string Content, bool IsError = false)
    {
        public static ToolResult Ok(string content) => new(content);
        public static ToolResult Err(string message) => new(message, IsError: true);
    }

    public sealed class ToolContext
    {
        public required string Cwd { get; init; }
        public required Loop.LoopContext LoopContext { get; init; }
        public Func<Loop.LoopEvent, Task>? EmitEvent { get; init; }
    }
}
