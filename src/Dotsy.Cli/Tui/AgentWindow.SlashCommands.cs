using Dotsy.Cli.SlashCommands;
using Dotsy.Cli.Tui.Approval;
using Dotsy.Cli.Tui.Colors;
using Dotsy.Cli.Tui.ToolList;
using Dotsy.Core.Config;
using Dotsy.Core.Utils;

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
            AppendConvo($"unknown command: /{cmd}  (try /{HelpCommand.CommandName})\n\n", Palette.Warn);
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

        completionFrame.SetScheme(Palette.FocusedPanelScheme());
        completionFrame.Visible = true;
        completionList.SelectedItem = 0;
        completionList.SetFocus();
        return true;
    }

    private void UpdateCompletionPopup(bool force = false)
    {
        var text = (promptInput.Text?.ToString() ?? "").TrimEnd('\r', '\n');
        if (!force && !completionFrame.Visible && !ShouldOpenCompletionPopup(text))
            return;

        var wasVisible = completionFrame.Visible;
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
        completionFrame.SetScheme(Palette.FocusedPanelScheme());
        completionFrame.Visible = true;

        if (completionList.SelectedItem is null || completionList.SelectedItem < 0 || completionList.SelectedItem >= completionItems.Count)
            completionList.SelectedItem = 0;
        completionList.EnsureSelectedItemVisible();
        completionFrame.SetNeedsDraw();

        if (!wasVisible)
            TuiSessionContext.App.Invoke(() => completionList.SetFocus());
    }

    private bool ShouldOpenCompletionPopup(string text)
    {
        if (!text.StartsWith('/') || text.Contains('\n'))
            return false;

        if (text == "/")
            return true;

        var body = text[1..];
        var firstSpace = body.IndexOf(' ');
        if (firstSpace < 0)
            return true;

        if (firstSpace == body.Length - 1)
            return true;

        return text.EndsWith('.') && BuildCompletionItems(text).Count > 0;
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
                .Where(c => c.StartsWithNoCase(prefix))
                .Select(c => new CompletionItem("/" + c, CommandReplacement(c)))
                .ToList();
        }

        var cmd = body[..firstSpace].ToLowerInvariant();
        var rest = body[(firstSpace + 1)..];
        // Argument completion is owned by the command itself (e.g. /add path completion).
        return (SlashCommands.Find(cmd)?.Complete(this, rest) ?? []).ToList();
    }

    private string? BuildInputHint()
    {
        var text = (promptInput.Text?.ToString() ?? "").TrimEnd('\r', '\n');
        if (text.Contains('\n') || !text.EndsWith(' '))
            return null;

        const string prefix = $"/{ConfigCommand.CommandName} ";
        if (!text.StartsWithNoCase(prefix))
            return null;

        var args = text[prefix.Length..].Trim();
        if (args.Length == 0 || args.Contains(' ') || !args.Contains('.'))
            return null;

        var param = ConfigEditor.FindParam(args);
        return param is null ? null : ConfigEditor.GetValueHint(param);
    }

    private string CommandReplacement(string name)
    {
        if (SlashCommands.Find(name) is not { } command)
            return "/" + name;

        var acceptsArguments = command.Usages.Any(u => u.Syntax.Contains('<') || u.Syntax.Contains('['));
        return "/" + name + (acceptsArguments ? " " : "");
    }

    private void MoveCompletionSelection(int delta)
    {
        if (completionItems.Count == 0) return;
        var current = completionList.SelectedItem.GetValueOrDefault();
        var next = delta switch
        {
            int.MinValue => 0,
            int.MaxValue => completionItems.Count - 1,
            _ => Math.Clamp(current + delta, 0, completionItems.Count - 1),
        };
        completionList.SelectedItem = next;
        completionList.EnsureSelectedItemVisible();
        completionList.SetNeedsDraw();
    }

    private bool EditCompletionFilter(Key key)
    {
        var text = (promptInput.Text?.ToString() ?? "").TrimEnd('\r', '\n');
        if (key.KeyCode == KeyCode.Backspace)
        {
            text = string.Concat(text.EnumerateRunes().SkipLast(1).Select(r => r.ToString()));
        }
        else if (IsPrintableChar(key))
        {
            text += key.AsRune.ToString();
        }
        else
        {
            return false;
        }

        promptInput.SetTextAndMoveEnd(text);
        ResizeInput();

        if (completionFrame.Visible)
            completionList.SetFocus();
        else
            promptInput.SetFocus();

        return true;
    }

    private bool TryHandleCompletionPopupKey(Key key)
    {
        if (!completionFrame.Visible)
            return false;

        if (key.KeyCode == KeyCode.Esc)
        {
            HideCompletionPopup();
            promptInput.SetFocus();
            return true;
        }

        if (key == Key.Tab || key.KeyCode == KeyCode.Enter)
        {
            ApplySelectedCompletion();
            return true;
        }

        if (key == Key.CursorUp)
        {
            MoveCompletionSelection(-1);
            return true;
        }

        if (key == Key.CursorDown)
        {
            MoveCompletionSelection(1);
            return true;
        }

        if (key == Key.Home)
        {
            MoveCompletionSelection(int.MinValue);
            return true;
        }

        if (key == Key.End)
        {
            MoveCompletionSelection(int.MaxValue);
            return true;
        }

        if (key == Key.PageUp)
        {
            MoveCompletionSelection(-ScrollMath.PageStep(completionList.Frame.Height));
            return true;
        }

        if (key == Key.PageDown)
        {
            MoveCompletionSelection(ScrollMath.PageStep(completionList.Frame.Height));
            return true;
        }

        return false;
    }

    private void ApplySelectedCompletion()
    {
        if (!completionFrame.Visible || completionItems.Count == 0)
            return;

        var idx = Math.Clamp(completionList.SelectedItem.GetValueOrDefault(), 0, completionItems.Count - 1);
        var item = completionItems[idx];
        promptInput.SetTextAndMoveEnd(item.Replacement);
        ResizeInput();
        promptInput.SetFocus();
        HideCompletionPopup();
    }

    private void HideCompletionPopup()
    {
        completionFrame.Visible = false;
        completionFrame.SetScheme(Palette.Scheme());
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
            // Cached wrapped rows are independent copies, so recoloring conversationLines
            // does not reach them; force a rebuild to pick up the new attributes.
            InvalidateConvoCache();
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
