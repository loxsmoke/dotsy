using Dotsy.Cli.SlashCommands;
using Dotsy.Cli.Tui.Approval;
using Dotsy.Cli.Tui.Colors;
using Dotsy.Cli.Tui.ToolList;

namespace Dotsy.Cli.Tui;

public partial class AgentWindow
{
    #region Slash command handling
    // Every slash command lives in the registry, carrying its own metadata, execution and completion
    // logic. The window only parses the input, enforces the shared "must be idle" guard, and forwards
    // to the command via the ISlashCommandHost it implements.
    private SlashCommandRegistry? slashCommands;
    private SlashCommandRegistry SlashCommands => slashCommands ??= SlashCommandRegistry.CreateDefault();

    private void HandleSlashCommand(string raw)
    {
        var parts = raw.TrimStart('/').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var cmd = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
        var rest = parts.Length > 1 ? string.Join(" ", parts[1..]) : "";

        if (SlashCommands.Find(cmd) is not { } command)
        {
            AppendConvo($"unknown command: /{cmd}  (try /help)\n\n", Palette.Warn);
            return;
        }

        if (command.RequiresIdle && scenarioCts is not null)
        {
            AppendConvo("cancel the current turn first\n\n", Palette.Warn);
            return;
        }

        command.Execute(this, rest);
    }


    #endregion

    #region Slash completions
    private bool TryShowCompletionPopup()
    {
        UpdateCompletionPopup(force: true);
        if (completionItems.Count == 0) return false;

        completionFrame.Visible = true;
        completionList.SelectedItem = 0;
        completionList.SetFocus();
        return true;
    }

    private void UpdateCompletionPopup(bool force = false)
    {
        if (!force && !completionFrame.Visible) return;

        var text = (promptInput.Text?.ToString() ?? "").TrimEnd('\r', '\n');
        var items = BuildCompletionItems(text);

        completionItems.Clear();
        foreach (var item in items)
            completionItems.Add(item);

        if (completionItems.Count == 0)
        {
            HideCompletionPopup();
            return;
        }

        var height = Math.Clamp(completionItems.Count + 2, 3, 9);
        var width = Math.Clamp(completionItems.Max(i => i.Display.Length) + 4, 20, 60);
        completionFrame.Height = height;
        completionFrame.Width = width;
        completionFrame.Y = Pos.AnchorEnd(inputHeight + height);
        completionFrame.Visible = true;

        if (completionList.SelectedItem is null || completionList.SelectedItem < 0 || completionList.SelectedItem >= completionItems.Count)
            completionList.SelectedItem = 0;
        completionList.EnsureSelectedItemVisible();
        completionFrame.SetNeedsDraw();
    }

    private List<CompletionItem> BuildCompletionItems(string text)
    {
        if (!text.StartsWith('/') || text.Contains('\n'))
            return [];

        var body = text[1..];
        var firstSpace = body.IndexOf(' ');
        if (firstSpace < 0)
        {
            var prefix = body;
            return SlashCommands.Names
                .Where(c => c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(c => new CompletionItem("/" + c, "/" + c + " "))
                .ToList();
        }

        var cmd = body[..firstSpace].ToLowerInvariant();
        var rest = body[(firstSpace + 1)..];
        // Argument completion is owned by the command itself (e.g. /add path completion).
        return (SlashCommands.Find(cmd)?.Complete(this, rest) ?? []).ToList();
    }

    private void MoveCompletionSelection(int delta)
    {
        if (completionItems.Count == 0) return;
        var current = completionList.SelectedItem.GetValueOrDefault();
        var next = Math.Clamp(current + delta, 0, completionItems.Count - 1);
        completionList.SelectedItem = next;
        completionList.EnsureSelectedItemVisible();
        completionList.SetNeedsDraw();
    }

    private void ApplySelectedCompletion()
    {
        if (!completionFrame.Visible || completionItems.Count == 0)
            return;

        var idx = Math.Clamp(completionList.SelectedItem.GetValueOrDefault(), 0, completionItems.Count - 1);
        var item = completionItems[idx];
        HideCompletionPopup();
        promptInput.SetTextAndMoveEnd(item.Replacement);
        ResizeInput();
        promptInput.SetFocus();
    }

    private void HideCompletionPopup()
    {
        completionFrame.Visible = false;
        completionItems.Clear();
        completionFrame.SetNeedsDraw();
    }
    #endregion

    #region Theme re-application
    // Re-applies the active theme to the whole view tree without a restart. Color attributes used
    // when drawing (Palette.*) resolve live, but Schemes are captured per view, so reassign
    // them here. When a recolor map is supplied, already-rendered cells (conversation scrollback,
    // tool output, file diffs) are remapped from the previous theme's attributes to the new ones.
    private void ReapplyTheme(Func<TGAttribute, TGAttribute>? recolor = null)
    {
        if (recolor is not null)
        {
            RecolorCellLines(conversationLines, recolor);
            foreach (var row in toolCallRows)
                if (row.Output is { } output) RecolorCellLines(output, recolor);
            foreach (var row in fileRows)
                RecolorCellLines(row.Diff, recolor);
        }

        SetScheme(Palette.Scheme());
        RethemeRecursive(this);
        HighlightFrameBorders(); // RethemeRecursive reset every frame to the base scheme; restore focus highlight
        ReloadConvo();
        SetNeedsDraw();
    }

    private static void RecolorCellLines(List<List<Cell>> lines, Func<TGAttribute, TGAttribute> recolor)
    {
        foreach (var line in lines)
            for (int j = 0; j < line.Count; j++)
            {
                var cell = line[j];
                if (cell.Attribute is { } a)
                    line[j] = cell with { Attribute = recolor(a) };
            }
    }

    private static void RethemeRecursive(View view)
    {
        foreach (var sv in view.SubViews)
        {
            switch (sv)
            {
                case StatusBar sb: sb.ApplyTheme(); break;
                case ApprovalView approval: approval.SetScheme(Palette.FocusedPanelScheme()); break;
                case FlatButton btn: btn.SetScheme(Palette.BtnScheme()); break;
                case InspectionFrameView inspect: inspect.SetScheme(Palette.FocusedPanelScheme()); break;
                case MultilineInput mi: mi.SetScheme(Palette.InputScheme()); break;
                case ScrollableText st: st.SetScheme(Palette.ReadOnlyTextScheme()); break;
                default: sv.SetScheme(Palette.Scheme()); break;
            }
            RethemeRecursive(sv);
        }
    }
    #endregion

}
