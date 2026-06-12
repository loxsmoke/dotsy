using System.Text.Json;

using Dotsy.Core.Utils;
using Dotsy.Core.Tools.Interfaces;

namespace Dotsy.Core.Tools;

public sealed class DoneTool : ITool
{
    private const int PanelSummaryLimit = 50;

    public string Name => "Done";
    public string Description => "Signal task completion with a summary. Ends the agent loop cleanly.";
    public JsonElement InputSchema => ToolSchemas.DoneSchema;
    public ToolSafety Safety => ToolSafety.ReadOnly;
    public bool IsCompletionSignal => true;

    public string FormatPanelArgument(JsonElement input, string cwd) =>
        AbbreviateSummary(input.GetStringPropertyOrEmpty("summary"));

    public string? FormatPanelResult(JsonElement input, string resultContent, string cwd) =>
        AbbreviateSummary(resultContent);

    public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var summary = input.GetStringPropertyOrEmpty("summary");
        return Task.FromResult(ToolResult.Ok(summary));
    }

    private static string AbbreviateSummary(string summary)
    {
        if (summary.Length <= PanelSummaryLimit)
            return summary;

        return summary[..PanelSummaryLimit] + "...";
    }
}
