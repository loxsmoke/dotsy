using Dotsy.Cli.SlashCommands.Interfaces;
using Dotsy.Cli.Tui.Colors;

namespace Dotsy.Cli.SlashCommands;

/// <summary>
/// <c>/help</c> — prints every slash command's syntax and description.
/// </summary>
internal sealed class HelpCommand : ISlashCommand
{
    public string Name => "help";

    public IReadOnlyList<string> Aliases => ["?"];

    public IReadOnlyList<SlashCommandUsage> Usages =>
    [
        new("/help", "Print the slash-command help text in the conversation panel."),
        new("/?", "Alias for /help."),
    ];

    public void Execute(ISlashCommandHost host, string args)
    {
        host.Write("Slash commands:\n", Palette.Bright);
        foreach (var usage in host.CommandUsages)
            host.WriteDescription(22, usage.Syntax, usage.Description, Palette.Cmd);
        host.Write("\n", Palette.Normal);
    }
}
