using Dotsy.Cli.Tui.Colors;
using Dotsy.Cli.Tui.Renderers;
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

        // Replay the full saved transcript (not just the post-compaction tail) so a resumed panel
        // shows the same conversation the live run did \u2014 every user prompt and every tool call.
        var messages = loaded.DisplayMessages;
        var toolInfo = loaded.ToolInfo;

        AppendConvo($"Resumed session: {loaded.SessionId}\n", Palette.Success);
        AppendConvo($"Messages loaded: {messages.Count}\n\n", Palette.Dim);

        var cwd = loaded.Cwd ?? TuiSessionContext.Cwd;
        var pendingTools = new Dictionary<string, RestoredTool>(StringComparer.Ordinal);
        var currentTurn = 0;
        var currentGroup = 0;

        foreach (var message in messages)
        {
            switch (message)
            {
                case UserMessage user:
                    // A real user prompt (has text, not just tool results) starts a new tool-call
                    // group, matching the live path where all of a prompt's calls share one group id
                    // so the panel draws grouping brackets around them.
                    if (user.Content.OfType<TextBlock>().Any(b => !string.IsNullOrWhiteSpace(b.Text)))
                    {
                        currentGroup = ++toolCallGroupSeq;
                        // The next AppendConvo writes the "User ›" line into the current tail line;
                        // anchor the group there so F2 can jump back to it.
                        groupConvoLine[currentGroup] = conversationLines.Count - 1;
                    }
                    RenderRestoredUser(user, pendingTools, toolInfo);
                    break;
                case AssistantMessage assistant:
                    RenderRestoredAssistant(assistant, cwd, pendingTools, currentTurn, currentGroup, toolInfo);
                    currentTurn++;
                    break;
            }
        }

        RefreshChangedFiles();
        ReloadConvo();
        ScrollToolListToEnd();
    }

    private void RenderRestoredUser(
        UserMessage user,
        Dictionary<string, RestoredTool> pendingTools,
        IReadOnlyDictionary<string, RestoredToolInfo> toolInfo)
    {
        var text = string.Join("\n", user.Content.OfType<TextBlock>().Select(b => b.Text))
            .TrimEnd();
        if (!string.IsNullOrWhiteSpace(text))
            AppendConvo($"User \u203a {text}\n\n", Palette.Cmd);

        foreach (var result in user.Content.OfType<ToolResultBlock>())
            ApplyRestoredToolResult(result, pendingTools, toolInfo);
    }

    private void RenderRestoredAssistant(
        AssistantMessage assistant,
        string cwd,
        Dictionary<string, RestoredTool> pendingTools,
        int turnNumber,
        int group,
        IReadOnlyDictionary<string, RestoredToolInfo> toolInfo)
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
                    RenderRestoredMarkdown(text.Text.TrimEnd());
                    AppendConvo("\n", Palette.Normal);
                    break;

                case ThinkingBlock thinking when !string.IsNullOrWhiteSpace(thinking.Thinking):
                    AppendConvo("Think \u203a ", Palette.Bullet);
                    AppendConvo(thinking.Thinking.TrimEnd() + "\n", Palette.Dim);
                    break;

                case ToolUseBlock toolUse:
                    AddRestoredToolUse(toolUse, cwd, pendingTools, turnNumber, group, toolInfo);
                    break;
            }
        }

        if (wroteAgentHeader)
            AppendConvo("\n", Palette.Normal);
    }

    // Render restored assistant text through the same markdown renderer the live stream uses, so a
    // resumed session keeps its headings/bold/inline-code/table/syntax highlighting instead of being
    // flattened to one colour.
    private void RenderRestoredMarkdown(string markdown)
    {
        var renderer = new MarkdownRenderer(convoWrapWidth, (text, attr) => AppendConvo(text, attr));
        renderer.Write(markdown);
        renderer.Flush();
    }

    private void AddRestoredToolUse(
        ToolUseBlock toolUse,
        string cwd,
        Dictionary<string, RestoredTool> pendingTools,
        int turnNumber,
        int group,
        IReadOnlyDictionary<string, RestoredToolInfo> toolInfo)
    {
        var rawArgs = toolUse.Input.GetRawText();
        var displayArg = FormatPanelArgument(toolUse.Name, rawArgs, cwd);
        var parameters = ExtractToolParameters(rawArgs);
        var startedAt = toolInfo.TryGetValue(toolUse.Id, out var meta) && meta.StartedAt is { } s
            ? s
            : DateTimeOffset.Now;

        var row = new ToolRow(
            toolUse.Name,
            displayArg,
            "PENDING",
            0,
            startedAt,
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
        Dictionary<string, RestoredTool> pendingTools,
        IReadOnlyDictionary<string, RestoredToolInfo> toolInfo)
    {
        if (!pendingTools.TryGetValue(result.ToolUseId, out var tool) ||
            tool.RowIndex < 0 ||
            tool.RowIndex >= toolCallRows.Count)
            return;

        // Mirror the live status/timing: OK/ERR/SKIP, and the recorded run duration (ms → whole
        // seconds, matching how the live path derives Elapsed).
        var status = result.IsError ? "ERR"
            : result.Content == "[skipped: duplicate]" ? "SKIP"
            : "OK";
        var elapsed = toolInfo.TryGetValue(result.ToolUseId, out var meta) ? meta.DurationMs / 1000 : 0;
        var row = toolCallRows[tool.RowIndex] with { Status = status, Elapsed = elapsed };
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
