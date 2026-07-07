using Dotsy.Cli.Tui.Approval;
using Dotsy.Cli.Tui.Colors;
using Dotsy.Cli.Tui.ToolList;
using Dotsy.Core.Tools;
using Dotsy.Core.Tools.Interfaces;
using Dotsy.Core.Utils;

namespace Dotsy.Cli.Tui;

public partial class AgentWindow
{
    private static readonly HashSet<string> WriteToolNames =
        new([WriteTool.ToolName, EditTool.ToolName, MultiEditTool.ToolName], StringComparer.OrdinalIgnoreCase);

    #region Approval overlay
    public Task<ApprovalChoice> ShowApproval(ITool tool, string displayArg)
    {
        return approvalView.ShowAsync(tool.Name, displayArg, tool.IsWriteTool);
    }
    private static string FormatRunApproval(ITool tool, string rawArgs, string cwd)
    {
        try
        {
            if (tool is null)
                return rawArgs;

            using var doc = System.Text.Json.JsonDocument.Parse(rawArgs);
            return tool.FormatRunApproval(doc.RootElement, cwd);
        }
        catch
        {
            return rawArgs;
        }
    }

    #endregion

    #region Tool call list handlers
    private void OnToolRowRender(object? sender, ListViewRowEventArgs e)
    {
        if (e.Row >= toolCallRows.Count) return;
        var row = toolCallRows[e.Row];
        var selected = toolCallList.HasFocus && toolCallList.SelectedItem == e.Row;
        e.RowAttribute = selected
            ? Palette.SelRow
            : row.Status switch
            {
                "OK" => Palette.Success,
                "ERR" => Palette.Err,
                "RUNNING" => Palette.Running,
                "SKIP" => Palette.Dim,
                _ => Palette.Dim
            };
    }

    private void OnToolSelected(object? sender, ListViewItemEventArgs e)
    {
        if (IsOverlayVisible()) return;
        if (e.Value is ToolRow row && row.Name.Length > 0)
            ShowInspect(row);
    }
    #endregion

    #region Tool call panel functions
    // ══ Public thread-safe tool API ═══════════════════════════════════════════

    public int AddTool(string name, string arg, string cwd = "", int group = 0, string? parameters = null, int? turnNumber = null)
    {
        var idx = Interlocked.Increment(ref toolCallCount) - 1;
        TuiSessionContext.App.Invoke(() =>
        {
            while (toolCallRows.Count <= idx)
                toolCallRows.Add(new ToolRow("", "", "PENDING", 0, DateTimeOffset.Now));
            toolCallRows[idx] = new ToolRow(name, arg, "RUNNING", 0, DateTimeOffset.Now, cwd, Group: group, Parameters: parameters, TurnNumber: turnNumber);
            ScrollToolListToEnd();
        });
        return idx;
    }

    // Keep the most recent tool visible as the list grows across prompts, mirroring how the
    // conversation panel tails its newest output. Does not change the selection.
    private void ScrollToolListToEnd()
    {
        int vh = Math.Max(1, toolCallList.Viewport.Height);
        toolCallList.TopItemCompat = Math.Max(0, toolCallRows.Count - vh);
        toolCallList.SetNeedsDraw();
    }

    public void UpdateTool(int idx, string status, int elapsedSec)
    {
        TuiSessionContext.App.Invoke(() =>
        {
            if (idx < toolCallRows.Count)
                toolCallRows[idx] = toolCallRows[idx] with { Status = status, Elapsed = elapsedSec };
        });
    }

    public void UpdateToolArg(int idx, string arg)
    {
        TuiSessionContext.App.Invoke(() =>
        {
            if (idx < toolCallRows.Count)
                toolCallRows[idx] = toolCallRows[idx] with { Arg = arg };
        });
    }

    private static List<List<Cell>> FormatToolOutput(string content)
    {
        var lines = new List<List<Cell>>();
        foreach (var line in content.Split('\n'))
            lines.Add(TextToCells(line, Palette.Normal));
        return lines;
    }

    public void SetToolOutput(int idx, List<List<Cell>> output)
    {
        TuiSessionContext.App.Invoke(() =>
        {
            if (idx < toolCallRows.Count)
                toolCallRows[idx] = toolCallRows[idx] with { Output = output };
        });
    }
    private static string FormatPanelArgument(string toolName, string rawArgs, string cwd)
    {
        try
        {
            if (TuiSessionContext.Registry?.TryGetTool(toolName, out var tool) != true || tool is null)
                return rawArgs;

            using var doc = System.Text.Json.JsonDocument.Parse(rawArgs);
            return tool.FormatPanelArgument(doc.RootElement, cwd);
        }
        catch
        {
            return rawArgs;
        }
    }

    private static string? FormatPanelResult(string toolName, string rawArgs, string resultContent, string cwd)
    {
        try
        {
            if (TuiSessionContext.Registry?.TryGetTool(toolName, out var tool) != true || tool is null)
                return null;

            using var doc = System.Text.Json.JsonDocument.Parse(rawArgs);
            return tool.FormatPanelResult(doc.RootElement, resultContent, cwd);
        }
        catch
        {
            return null;
        }
    }

    private void ShowInspect(ToolRow row)
    {
        inspectReturnFocus = toolCallList;
        inspectFrame.Title = row.TurnNumber is { } turnNumber
            ? $" Inspection. Turn {turnNumber}  [ Esc close ] "
            : " Inspection  [ Esc close ] ";

        var lines = new List<List<Cell>>();
        void AddLine(string text, TGAttribute attr) => lines.Add(TextToCells(text, attr));

        AddLine($"  Tool     {row.Name}", Palette.Bright);
        AddLine($"  Args     {row.Arg}", Palette.Normal);
        if (!string.IsNullOrEmpty(row.Cwd))
            AddLine($"  Folder   {row.Cwd}", Palette.Dim);
        if (row.Parameters != null)
        {
            AddLine("  Parameters:", Palette.Bright);
            // Format parameters as key-value pairs
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(row.Parameters);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    AddLine($"    {prop.Name}: {prop.Value.GetString() ?? ""}", Palette.Normal);
                }
            }
            catch
            {
                // If parsing fails, just show the raw parameters
                AddLine($"    {row.Parameters}", Palette.Normal);
            }
        }
        AddLine("", Palette.Normal);
        var sAttr = row.Status switch { "OK" => Palette.Success, "ERR" => Palette.Err, _ => Palette.Warn };
        AddLine($"  Status   {row.Status}", sAttr);
        AddLine($"  Elapsed  {row.Elapsed}s", Palette.Normal);
        AddLine($"  Started  {row.StartedAt:HH:mm:ss}", Palette.Normal);

        AddLine("", Palette.Normal);

        // Show output if available
        if (row.Output is { Count: > 0 } output)
        {
            AddLine("  Output:", Palette.Bright);
            lines.Add([]);
            lines.AddRange(output);
        }
        else
        {
            AddLine("  (no output recorded)", Palette.Dim);
        }

        inspectText.LoadText(lines);
        ShowInspectFrame();
    }

    private void ShowInspectFrame()
    {
        promptInput.Visible = false;
        inspectText.ScrollTo(new System.Drawing.Point(0, 0));
        inspectFrame.Visible = true;
        inspectText.SetFocus();
    }

    private void HideInspect()
    {
        inspectFrame.Visible = false;
        if (approvalView.Visible)
        {
            approvalView.FocusFirstButton();
        }
        else
        {
            promptLabel.Visible = true;
            promptInput.Visible = true;
            (inspectReturnFocus ?? toolCallList).SetFocus();
        }
    }

    // Builds a colored inspect view for Edit / MultiEdit showing output then input parameters.
    private static List<List<Cell>> FormatEditInspectCells(string toolName, string argsJson, string resultContent, string cwd)
    {
        var lines = new List<List<Cell>>();

        List<Cell> Row(string text, TGAttribute attr) => TextToCells(text, attr);

        void Blank() => lines.Add([]);

        void Header(string title) => lines.Add(Row($"  {title}", Palette.Bright));

        void LabelValue(string label, string value, TGAttribute valueAttr)
        {
            var line = TextToCells($"  {label,-10}", Palette.Dim);
            line.AddRange(TextToCells(value, valueAttr));
            lines.Add(line);
        }

        void TextBlock(string sectionTitle, string text, TGAttribute lineAttr)
        {
            Header(sectionTitle);
            foreach (var rawLine in text.Split('\n'))
                lines.Add(Row("  " + rawLine.TrimEnd('\r'), lineAttr));
        }

        void EditSection(System.Text.Json.JsonElement edit)
        {
            if (edit.TryGetProperty("start_line", out var sl) && edit.TryGetProperty("end_line", out var el))
            {
                LabelValue("Lines", $"{sl.GetInt32()} – {el.GetInt32()}", Palette.Normal);
            }
            else
            {
                var old = edit.GetStringPropertyOrEmpty("old_string");
                Blank();
                TextBlock("Search:", old, Palette.Warn);
            }
            var @new = edit.GetStringPropertyOrEmpty("new_string");
            Blank();
            TextBlock("Replace:", @new, Palette.Success);
        }

        if (!string.IsNullOrWhiteSpace(resultContent))
        {
            Header("Output");
            foreach (var rawLine in resultContent.TrimEnd().Split('\n'))
                lines.Add(Row("  " + rawLine.TrimEnd('\r'), Palette.Normal));
            Blank();
        }

        System.Text.Json.JsonElement input;
        try { input = ToolArgs.TryParseArgs(argsJson); }
        catch { return lines; }

        var path = input.GetStringPropertyOrEmpty("path");
        LabelValue("Path", PathDisplay.MakeRelative(path, cwd), Palette.Normal);

        if (toolName == EditTool.ToolName)
        {
            EditSection(input);
        }
        else if (toolName == MultiEditTool.ToolName &&
                 input.TryGetProperty("edits", out var editsEl) &&
                 editsEl.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            var idx = 1;
            foreach (var edit in editsEl.EnumerateArray())
            {
                Blank();
                Header($"Edit {idx}");
                EditSection(edit);
                idx++;
            }
        }

        return lines;
    }

    #endregion
}
