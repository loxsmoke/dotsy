using System.Text.Json;
using Dotsy.Core.Utils;

using Dotsy.Core.Tools.Interfaces;
using Dotsy.Core.Loop.Data;

namespace Dotsy.Core.Tools;

public sealed class AskTool : ITool
{
    public const string ToolName = "Ask";
    public string Name => ToolName;
    public string Description => "Pause and ask the user a question. Returns the user's answer.";
    public JsonElement InputSchema => ToolSchemas.AskSchema;
    public ToolSafety Safety => ToolSafety.ReadOnly;
    public bool IsCompletionSignal => false;

    public string FormatPanelArgument(JsonElement input, string cwd) =>
        input.GetStringPropertyOrEmpty("question");

    public async Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var question = input.GetProperty("question").GetString() ?? "";

        if (ctx.EmitEvent is null)
            return ToolResult.Err("Ask is not available in this context");

        var tcs = new TaskCompletionSource<PermissionDecision>();
        var ev = new PermissionRequired(this, Name, question, tcs);
        await ctx.EmitEvent(ev);

        // Wait for user decision — we reuse PermissionDecision as the pause mechanism
        // and interpret any non-Deny as "answered"
        var decision = await tcs.Task.WaitAsync(ct);

        return decision == PermissionDecision.Deny
            ? ToolResult.Err("User declined to answer")
            : ToolResult.Ok("User acknowledged");
    }
}
