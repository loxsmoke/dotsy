using Dotsy.Cli.SlashCommands.Interfaces;
using Dotsy.Cli.Tui;
using Dotsy.Cli.Tui.Colors;
using Dotsy.Core.Loop.Data;
using Dotsy.Core.Session;

namespace Dotsy.Cli.SlashCommands;

/// <summary>
/// <c>/clear</c> — starts a fresh session and clears the visible conversation, tool log and
/// changed-files panel. The session swap is done here; the view reset is delegated to the host.
/// </summary>
internal sealed class ClearCommand : ISlashCommand
{
    public string Name => "clear";

    public IReadOnlyList<SlashCommandUsage> Usages =>
    [
        new("/clear", "Clear the visible conversation panel, tool log, and changed-files panel for the current TUI view."),
    ];

    public void Execute(ISlashCommandHost host, string args)
    {
        // Start a fresh session
        var config = TuiSessionContext.Config;
        var cwd = TuiSessionContext.Cwd;
        var sessionDir = SessionStore.ResolveDir(config.Session.LogDir, cwd);
        var newSessionId = SessionStore.NextId(sessionDir);
        TuiSessionContext.Session = new SessionStore(newSessionId, sessionDir);
        TuiSessionContext.LoopCtx = new LoopContext(newSessionId);

        host.ResetConversationView();
        host.Write("Started a fresh session\n\n", Palette.Success);
        host.UpdateStatusBarFromCtx();
    }
}
