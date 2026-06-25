using Dotsy.Cli.SlashCommands.Interfaces;
using Dotsy.Cli.Tui;
using Dotsy.Cli.Tui.Colors;

namespace Dotsy.Cli.SlashCommands;

/// <summary>
/// <c>/verbose</c> — toggles whether tool calls and results appear inline in the conversation.
/// </summary>
internal sealed class VerboseCommand : ISlashCommand
{
    public string Name => "verbose";

    public IReadOnlyList<SlashCommandUsage> Usages =>
    [
        new("/verbose", "Toggle inline verbose mode for tool calls and tool results."),
    ];

    public void Execute(ISlashCommandHost host, string args)
    {
        var cfg = TuiSessionContext.Config;
        cfg.Tui.Verbose = !cfg.Tui.Verbose;
        var state = cfg.Tui.Verbose ? "on" : "off";
        host.Write(
            $"  verbose {state}  (tool calls and results will{(cfg.Tui.Verbose ? "" : " not")} appear inline)\n\n",
            Palette.Dim);
    }
}
