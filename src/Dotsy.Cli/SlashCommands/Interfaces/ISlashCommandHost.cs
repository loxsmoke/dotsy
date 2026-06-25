namespace Dotsy.Cli.SlashCommands.Interfaces;

using Dotsy.Core.Session.Data;

/// <summary>
/// The capability surface a slash command needs from its UI host. <see cref="AgentWindow"/>
/// implements this so commands can be written and tested without referencing the window's
/// private state. Session, config, tool-registry and permission state is read from the static
/// <c>TuiSessionContext</c> instead of being threaded through here.
/// </summary>
internal interface ISlashCommandHost
{
    #region Output
    /// <summary>Append coloured text to the conversation panel.</summary>
    void Write(string text, TGAttribute color);

    /// <summary>Append a formatted error block to the conversation panel.</summary>
    void WriteError(string message);

    /// <summary>
    /// Writes a two-column "name + description" row to the conversation panel: <paramref name="name"/>
    /// is padded to <paramref name="nameWidth"/> columns, and <paramref name="description"/> is
    /// word-wrapped to the panel width with continuation lines aligned under the description. Owns all
    /// padding and wrapping so callers don't need the panel width. <paramref name="nameColor"/> tints
    /// the name column (defaults to the bright/heading colour).
    /// </summary>
    void WriteDescription(int nameWidth, string name, string description, TGAttribute? nameColor = null);
    #endregion

    #region Command catalogue
    /// <summary>Usage lines for every registered slash command (drives /help and /self).</summary>
    IReadOnlyList<SlashCommandUsage> CommandUsages { get; }
    #endregion

    #region Turn / status
    /// <summary>True while an agent turn is in flight.</summary>
    bool IsBusy { get; }

    void SetState(string state);
    void SetModel(string id);
    void SetSession(string id);
    void UpdateStatusBarFromCtx();
    void StartSpinner(string state);
    void StopSpinner(string state);
    #endregion

    #region View operations (intrinsically window-owned)
    /// <summary>Clear the conversation buffer, tool log and changed-files panel.</summary>
    void ResetConversationView();

    /// <summary>Clear the tool log and changed-files panel, leaving the conversation intact.</summary>
    void ResetToolAndFilePanels();

    /// <summary>Re-read the working tree and repopulate the changed-files panel.</summary>
    void RefreshChangedFiles();

    /// <summary>Render a loaded session's saved chat and tool history into the host view.</summary>
    void RenderLoadedSession(LoadedSession loaded);

    /// <summary>
    /// Applies a colour theme by name, recolouring already-rendered content. Returns the resolved
    /// theme name and whether the requested name was unknown (fell back to the default).
    /// </summary>
    (string Resolved, bool FellBack) ApplyTheme(string value);
    #endregion
   

    #region Prompt pipeline
    void SubmitUserPrompt(string displayText, string promptText);
    void AddPromptHistory(string entry);
    #endregion

    #region Turn lifecycle
    /// <summary>
    /// Starts a cancellable background operation tied to the window's Ctrl+C cancel mechanism and
    /// returns its token. Pair with <see cref="EndScenario"/> in a finally block.
    /// </summary>
    CancellationToken BeginScenario();

    /// <summary>Ends the operation started by <see cref="BeginScenario"/>.</summary>
    void EndScenario();
    #endregion

    // ── Application ────────────────────────────────────────────────────────
    /// <summary>Quit the TUI.</summary>
    void RequestStop();

    // ── Threading ──────────────────────────────────────────────────────────
    /// <summary>Marshal an action onto the Terminal.Gui main loop.</summary>
    void Invoke(Action action);
}
