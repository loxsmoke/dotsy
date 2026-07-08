using Dotsy.Cli.SlashCommands.Interfaces;

namespace Dotsy.Cli.SlashCommands;

/// <summary>
/// <c>/exit</c> (alias <c>/quit</c>) — quits the TUI.
/// </summary>
internal sealed class ExitCommand : ISlashCommand
{
    public string Name => "exit";

    public IReadOnlyList<string> Aliases => ["quit"];

    public IReadOnlyList<SlashCommandUsage> Usages =>
        [new($"/{Name}", "Quit the TUI."), ..Aliases.Select(a => new SlashCommandUsage($"/{a}", $"Alias for /{Name}."))];

    public void Execute(ISlashCommandHost host, string args) => host.RequestStop();
}
