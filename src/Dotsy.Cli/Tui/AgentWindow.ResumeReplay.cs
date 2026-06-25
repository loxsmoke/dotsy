using Dotsy.Cli.Tui.Colors;
using Dotsy.Cli.Tui.ToolList;
using Dotsy.Core.Providers;
using Dotsy.Core.Session.Data;
using Dotsy.Core.Tools;

namespace Dotsy.Cli.Tui;

public partial class AgentWindow
{
    private void RenderLoadedSession(LoadedSession loaded)
    {
        ResetConversationView();

        AppendConvo($"Resumed session: {loaded.SessionId}\n", Palette.Success);
        AppendConvo($"Messages loaded: {loaded.Messages.Count}\n\n", Palette.Dim);

        var cwd = loaded.Cwd ?? TuiSessionContext.Cwd;
        var pendingTools = new Dictionary<string, RestoredTool>(StringComparer.Ordinal);
        var currentTurn = 0;

        foreach (var message in loaded.Messages)
        {
            switch (message)
            {
                case UserMessage user:
                    RenderRestoredUser(user, pendingTools);
                    break;
                case AssistantMessage assistant:
                    RenderRestoredAssistant(assistant, cwd, pendingTools, currentTurn);
                    currentTurn++;
                    break;
            }
        }

        RefreshChangedFiles();
        ReloadConvo();
        ScrollToolListToEnd();
    }

    private void RenderRestoredUser(UserMessage user, Dictionary<string, RestoredTool> pendingTools)
    {
        var text = string.Join("\n", user.Content.OfType<TextBlock>().Select(b => b.Text))
            .TrimEnd();
        if (!string.IsNullOrWhiteSpace(text))
            AppendConvo($"User \u203a {text}\n\n", Palette.Cmd);

        foreach (var result in user.Content.OfType<ToolResultBlock>())
            ApplyRestoredToolResult(result, pendingTools);
    }

    private void RenderRestoredAssistant(
        AssistantMessage assistant,
        string cwd,
        Dictionary<string, RestoredTool> pendingTools,
        int turnNumber)
    {
        var wroteAgentHeader = false;
        foreach (var block in assistant.Content)
        {
            switch (block)
            {
                case TextBlock text when !string.IsNullOrWhiteSpace(text.Text):
                    if (!wroteAgentHeader)
                    {
                        AppendConvo("Agent \u203a ", Palette.Bullet);
                        wroteAgentHeader = true;
                    }
                    AppendConvo(text.Text.TrimEnd() + "\n", Palette.Normal);
                    break;

                case ThinkingBlock thinking when !string.IsNullOrWhiteSpace(thinking.Thinking):
                    AppendConvo("Think \u203a ", Palette.Dim);
                    AppendConvo(thinking.Thinking.TrimEnd() + "\n", Palette.Dim);
                    break;

                case ToolUseBlock toolUse:
                    AddRestoredToolUse(toolUse, cwd, pendingTools, turnNumber);
                    break;
            }
        }

        if (wroteAgentHeader)
            AppendConvo("\n", Palette.Normal);
    }

    private void AddRestoredToolUse(
        ToolUseBlock toolUse,
        string cwd,
        Dictionary<string, RestoredTool> pendingTools,
        int turnNumber)
    {
        var rawArgs = toolUse.Input.GetRawText();
        var displayArg = FormatPanelArgument(toolUse.Name, rawArgs, cwd);
        var group = ++toolCallGroupSeq;
        var parameters = ExtractToolParameters(rawArgs);

        var row = new ToolRow(
            toolUse.Name,
            displayArg,
            "PENDING",
            0,
            DateTimeOffset.Now,
            cwd,
            Group: group,
            Parameters: parameters,
            TurnNumber: turnNumber);

        if (toolUse.Name == WriteTool.ToolName &&
            ToolPanelFormatter.GetWriteContent(rawArgs) is { } content)
            row = row with { Output = FormatToolOutput(content) };

        toolCallRows.Add(row);
        var rowIndex = toolCallRows.Count - 1;
        toolCallCount = toolCallRows.Count;
        pendingTools[toolUse.Id] = new RestoredTool(rowIndex, toolUse.Name, rawArgs, cwd);
    }

    private void ApplyRestoredToolResult(
        ToolResultBlock result,
        Dictionary<string, RestoredTool> pendingTools)
    {
        if (!pendingTools.TryGetValue(result.ToolUseId, out var tool) ||
            tool.RowIndex < 0 ||
            tool.RowIndex >= toolCallRows.Count)
            return;

        var status = result.IsError ? "ERR" : "OK";
        var row = toolCallRows[tool.RowIndex] with { Status = status };
        var output = BuildRestoredToolOutput(tool, result);
        if (output is { Count: > 0 })
            row = row with { Output = output };

        if (!result.IsError &&
            FormatPanelResult(tool.Name, tool.ArgsJson, result.Content, tool.Cwd) is { } enriched)
            row = row with { Arg = enriched };

        toolCallRows[tool.RowIndex] = row;
    }

    private static List<List<Cell>> BuildRestoredToolOutput(RestoredTool tool, ToolResultBlock result)
    {
        if (!result.IsError &&
            tool.Name is EditTool.ToolName or MultiEditTool.ToolName)
            return FormatEditInspectCells(tool.Name, tool.ArgsJson, result.Content, tool.Cwd);

        if (!result.IsError &&
            tool.Name == WriteTool.ToolName &&
            ToolPanelFormatter.GetWriteContent(tool.ArgsJson) is { } content)
            return FormatToolOutput(content);

        return FormatToolOutput(result.Content);
    }

    private static string? ExtractToolParameters(string argsJson)
    {
        try
        {
            if (argsJson.Length <= 2 || !argsJson.StartsWith('{') || !argsJson.EndsWith('}'))
                return null;

            using var doc = System.Text.Json.JsonDocument.Parse(argsJson);
            if (doc.RootElement.TryGetProperty("path", out var pathElement))
                return $"path: {pathElement.GetString() ?? ""}";
            if (doc.RootElement.TryGetProperty("content", out var contentElement))
            {
                var content = contentElement.GetString() ?? "";
                return $"content: {content.Length} chars";
            }

            var props = new List<string>();
            foreach (var prop in doc.RootElement.EnumerateObject())
                props.Add($"{prop.Name}: {prop.Value.GetString() ?? prop.Value.GetRawText()}");
            return props.Count > 0 ? string.Join(", ", props) : null;
        }
        catch
        {
            return null;
        }
    }

    private sealed record RestoredTool(int RowIndex, string Name, string ArgsJson, string Cwd);
}
