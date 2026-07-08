using Dotsy.Cli.SlashCommands.Interfaces;
using Dotsy.Cli.Tui;
using Dotsy.Cli.Tui.Colors;
using Dotsy.Core.Loop;
using Dotsy.Core.Loop.Data;

namespace Dotsy.Cli.SlashCommands;

/// <summary>
/// <c>/sec</c> — renders a summary of the tool permissions currently in effect.
/// </summary>
internal sealed class SecCommand : ISlashCommand
{
    public const string CommandName = "sec";
    public string Name => CommandName;

    public IReadOnlyList<SlashCommandUsage> Usages =>
    [
        new($"/{Name}", "Show a security summary of the tool permissions currently in effect."),
    ];

    public void Execute(ISlashCommandHost host, string args)
    {
        var permissions = TuiSessionContext.Permissions;
        if (permissions is null)
        {
            host.WriteError("permission store not initialized");
            return;
        }

        var summary = SecuritySummaryRenderer.Render(new SecuritySummaryRequest(
            TuiSessionContext.Config,
            permissions,
            TuiSessionContext.Cwd,
            TuiSessionContext.Registry,
            TuiSessionContext.LoopCtx,
            Headless: false));

        host.Write(summary + "\n\n", Palette.Normal);
    }
}
