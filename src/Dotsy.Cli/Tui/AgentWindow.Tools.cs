using Dotsy.Core.Tools;
using Dotsy.Core.Utils;
using Terminal.Gui;
using TGAttribute = Terminal.Gui.Attribute;

namespace Dotsy.Cli.Tui;

public partial class AgentWindow
{
    private static readonly HashSet<string> WriteToolNames =
        new([WriteTool.ToolName, EditTool.ToolName, MultiEditTool.ToolName], StringComparer.OrdinalIgnoreCase);

    #region Approval overlay
    public async Task<ApprovalChoice> ShowApproval(string toolName, string displayArg)
    {
        approvalTcs = new TaskCompletionSource<ApprovalChoice>();
        Application.Invoke(() =>
        {
            approvalMsg.Text = $"  {toolName}  {displayArg}";
            if (btnProject is not null)
                btnProject.Visible = WriteToolNames.Contains(toolName);
            approvalFrame.Visible = true;
            FocusFirstApprovalButton();
        });
        return await approvalTcs.Task;
    }
    private static string FormatRunApproval(string toolName, string rawArgs, string cwd)
    {
        try
        {
            if (TuiSessionContext.Registry?.TryGetTool(toolName, out var tool) != true || tool is null)
                return rawArgs;

            using var doc = System.Text.Json.JsonDocument.Parse(rawArgs);
            return tool.FormatRunApproval(doc.RootElement, cwd);
        }
        catch
        {
            return rawArgs;
        }
    }

    private void AcceptApproval(ApprovalChoice choice)
    {
        Application.Invoke(() =>
        {
            approvalFrame.Visible = false;
            promptInput.SetFocus();
        });
        approvalTcs?.TrySetResult(choice);
    }

    // Visible buttons of the approval overlay, in display order.
    private List<FlatButton> ApprovalButtons() =>
        approvalFrame.Subviews.OfType<FlatButton>().Where(b => b.Visible).ToList();

    private void FocusFirstApprovalButton()
    {
        var first = ApprovalButtons().FirstOrDefault();
        if (first is not null) first.SetFocus();
        else approvalFrame.SetFocus();
    }

    private void CycleApprovalFocus(bool back)
    {
        var buttons = ApprovalButtons();
        if (buttons.Count == 0) { approvalFrame.SetFocus(); return; }
        var focused = Application.Navigation?.GetFocused();
        int idx = buttons.FindIndex(b => b == focused);
        int next = idx < 0 ? 0 : ((idx + (back ? -1 : 1)) % buttons.Count + buttons.Count) % buttons.Count;
        buttons[next].SetFocus();
    }
    #endregion

    #region Tool call list handlers
    private void OnToolRowRender(object? sender, ListViewRowEventArgs e)
    {
        if (e.Row >= toolCallRows.Count) return;
        var row = toolCallRows[e.Row];
        var selected = toolCalllList.HasFocus && toolCalllList.SelectedItem == e.Row;
        e.RowAttribute = selected
            ? new TGAttribute(ColorName16.White, ColorName16.DarkGray)
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
        if (e.Value is ToolRow row && row.Name.Length > 0)
            ShowInspect(row);
    }
    #endregion


    #region Tool call panel functions
    // ══ Public thread-safe tool API ═══════════════════════════════════════════

    public int AddTool(string name, string arg, string cwd = "", int group = 0, string? parameters = null)
    {
        var idx = Interlocked.Increment(ref toolCallCount) - 1;
        Application.Invoke(() =>
        {
            while (toolCallRows.Count <= idx)
                toolCallRows.Add(new ToolRow("", "", "PENDING", 0, DateTimeOffset.Now));
            toolCallRows[idx] = new ToolRow(name, arg, "RUNNING", 0, DateTimeOffset.Now, cwd, Group: group, Parameters: parameters);
            ScrollToolListToEnd();
        });
        return idx;
    }

    // Keep the most recent tool visible as the list grows across prompts, mirroring how the
    // conversation panel tails its newest output. Does not change the selection.
    private void ScrollToolListToEnd()
    {
        int vh = Math.Max(1, toolCalllList.Viewport.Height);
        toolCalllList.TopItem = Math.Max(0, toolCallRows.Count - vh);
        toolCalllList.SetNeedsDraw();
    }

    public void UpdateTool(int idx, string status, int elapsedSec)
    {
        Application.Invoke(() =>
        {
            if (idx < toolCallRows.Count)
                toolCallRows[idx] = toolCallRows[idx] with { Status = status, Elapsed = elapsedSec };
        });
    }

    public void UpdateToolArg(int idx, string arg)
    {
        Application.Invoke(() =>
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
        Application.Invoke(() =>
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

        inspectText.Load(lines);
        ShowInspectFrame();
    }

    private void ShowInspectFrame()
    {
        promptInput.Visible = false;
        inspectText.ScrollTo(0);
        inspectText.ScrollTo(0, false);
        inspectFrame.Visible = true;
        inspectText.SetFocus();
    }

    private void HideInspect()
    {
        inspectFrame.Visible = false;
        promptLabel.Visible = true;
        promptInput.Visible = true;
        toolCalllList.SetFocus();
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
        try { input = System.Text.Json.JsonDocument.Parse(argsJson).RootElement; }
        catch { return lines; }

        var path = input.GetStringPropertyOrEmpty("path");
        LabelValue("Path", MakeRelCwd(path, cwd), Palette.Normal);

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

    private static string MakeRelCwd(string path, string cwd)
    {
        if (string.IsNullOrEmpty(path)) return ".";
        try
        {
            var abs = Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(cwd, path));
            var cwdFull = Path.GetFullPath(cwd);
            if (abs.StartsWith(cwdFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return abs[(cwdFull.Length + 1)..];
            if (abs.Equals(cwdFull, StringComparison.OrdinalIgnoreCase))
                return ".";
        }
        catch { }
        return path;
    }
    #endregion
}
