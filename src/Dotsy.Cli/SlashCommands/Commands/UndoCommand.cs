using Dotsy.Cli.SlashCommands.Interfaces;
using Dotsy.Cli.Tui;
using Dotsy.Core.Git;

namespace Dotsy.Cli.SlashCommands;

/// <summary>
/// <c>/undo</c> — resets tracked files to the previous git checkpoint for the current session turn.
/// </summary>
internal sealed class UndoCommand : ISlashCommand
{
    public string Name => "undo";

    public bool RequiresIdle => true;

    public IReadOnlyList<SlashCommandUsage> Usages =>
    [
        new("/undo", "Reset tracked files to the previous git checkpoint for the current session turn, if one exists."),
    ];

    public void Execute(ISlashCommandHost host, string args)
    {
        var ctx = TuiSessionContext.LoopCtx;
        if (ctx is null)
        {
            host.WriteError("session context not initialized");
            return;
        }

        var git = new GitIntegration(TuiSessionContext.Cwd);
        if (git.Undo(ctx.SessionId, ctx.TurnCount))
        {
            host.Write("undone to previous checkpoint\n\n", Palette.Success);
            host.RefreshChangedFiles();
        }
        else
        {
            host.Write("no checkpoint found for this session\n\n", Palette.Warn);
        }
    }
}
