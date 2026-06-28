using Dotsy.Cli.SlashCommands.Interfaces;
using Dotsy.Cli.Tui.Colors;
using Dotsy.Core.Session.Data;

namespace Dotsy.Cli.Tui;

/// <summary>
/// <see cref="AgentWindow"/>'s implementation of <see cref="ISlashCommandHost"/>. These members are
/// thin forwarders onto the window's existing private helpers so that slash commands can be written
/// against the host abstraction instead of reaching into the window's fields.
/// </summary>
public partial class AgentWindow : ISlashCommandHost
{
    void ISlashCommandHost.Write(string text, TGAttribute color) => AppendConvo(text, color);

    void ISlashCommandHost.WriteError(string message) => AppendConvoError(message);

    void ISlashCommandHost.WriteDescription(int nameWidth, string name, string description, TGAttribute? nameColor) =>
        WriteDescription(nameWidth, name, description, nameColor);

    private void WriteDescription(int nameWidth, string name, string description, TGAttribute? nameColor = null)
    {
        const int indent = 2;
        var nameCol = indent + nameWidth;
        var descWidth = Math.Max(20, convo.Frame.Width - nameCol);
        var contIndent = new string(' ', nameCol);
        var lines = WordWrap(description, descWidth);

        AppendConvo(new string(' ', indent) + name.PadRight(nameWidth), nameColor ?? Palette.Bright);
        AppendConvo(lines[0] + "\n", Palette.Normal);
        for (var i = 1; i < lines.Count; i++)
            AppendConvo(contIndent + lines[i] + "\n", Palette.Normal);
    }

    bool ISlashCommandHost.IsBusy => scenarioCts is not null;

    void ISlashCommandHost.SetState(string state) => SetStatus(state);

    void ISlashCommandHost.SetModel(string id) => TuiSessionContext.App.Invoke(() => statusBar.SetModel(id));

    void ISlashCommandHost.SetSession(string id) => TuiSessionContext.App.Invoke(() => statusBar.SetSession(id));

    void ISlashCommandHost.UpdateStatusBarFromCtx() => UpdateStatusBarFromCtx();

    void ISlashCommandHost.StartSpinner(string state) => StartSpinner(state);

    void ISlashCommandHost.StopSpinner(string state) => StopSpinner(state);

    void ISlashCommandHost.RefreshChangedFiles() => RefreshChangedFiles();

    void ISlashCommandHost.RenderLoadedSession(LoadedSession loaded) => RenderLoadedSession(loaded);

    IReadOnlyList<SlashCommandUsage> ISlashCommandHost.CommandUsages => SlashCommands.Usages;

    (string Resolved, bool FellBack) ISlashCommandHost.ApplyTheme(string value)
    {
        var previousTheme = Palette.ActiveTheme;
        var (resolved, fellBack) = Palette.Apply(value);
        var recolor = Palette.BuildRecolorMap(previousTheme);
        TuiSessionContext.App.Invoke(() => ReapplyTheme(recolor));
        return (resolved, fellBack);
    }

    void ISlashCommandHost.SubmitUserPrompt(string displayText, string promptText) =>
        SubmitUserPrompt(displayText, promptText);

    void ISlashCommandHost.AddPromptHistory(string entry) => promptHistory.Add(entry);

    void ISlashCommandHost.RefreshCompletions()
    {
        if (completionFrame.Visible)
            UpdateCompletionPopup(force: true);
    }

    CancellationToken ISlashCommandHost.BeginScenario()
    {
        scenarioCts = new CancellationTokenSource();
        return scenarioCts.Token;
    }

    void ISlashCommandHost.EndScenario()
    {
        scenarioCts?.Dispose();
        scenarioCts = null;
    }

    void ISlashCommandHost.RequestStop() => TuiSessionContext.App.RequestStop();

    void ISlashCommandHost.Invoke(Action action) => TuiSessionContext.App.Invoke(action);

    /// <summary>
    /// Clears the conversation buffer, tool log and changed-files panel, returning the left pane to
    /// its full-height layout. The session swap that precedes this lives in the calling command.
    /// </summary>
    public void ResetConversationView()
    {
        conversationLines.Clear();
        conversationLines.Add([]);
        noWrapLineIndices.Clear();
        InvalidateConvoCache();
        lock (streamCursorLock)
            streamCursorVisible = false;
        ReloadConvo();
        ResetToolAndFilePanels();
        leftFrame.SetNeedsDraw();
    }

    /// <summary>
    /// Clears the tool log and changed-files panel, returning the left pane to its full-height layout,
    /// but leaves the conversation scrollback untouched (used by /resume).
    /// </summary>
    public void ResetToolAndFilePanels()
    {
        toolCallRows.Clear();
        toolCallCount = 0;
        toolCallGroupSeq = 0;
        fileRows.Clear();
        fileFrame.Visible = false;
        convo.Height = Dim.Fill();
    }
}
