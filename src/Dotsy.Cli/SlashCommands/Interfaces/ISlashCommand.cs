using Dotsy.Cli.Tui;

namespace Dotsy.Cli.SlashCommands.Interfaces;

/// <summary>
/// A slash command that can be dispatched independently of <see cref="AgentWindow"/>.
/// Commands depend only on <see cref="ISlashCommandHost"/> for UI side effects and read
/// session/config/registry state from the static <c>TuiSessionContext</c>; they never touch
/// the window directly. This keeps each command self-contained and unit-testable.
/// </summary>
internal interface ISlashCommand
{
    /// <summary>Primary name, lower-case, without the leading slash (e.g. <c>"add"</c>).</summary>
    string Name { get; }

    /// <summary>Alternative names that dispatch to this command (e.g. <c>"quit"</c> → exit).</summary>
    IReadOnlyList<string> Aliases => [];

    /// <summary>Usage lines for <c>/help</c>. The first entry is the canonical form.</summary>
    IReadOnlyList<SlashCommandUsage> Usages { get; }

    /// <summary>When true, the command is rejected while an agent turn is running.</summary>
    bool RequiresIdle => false;

    /// <summary>Runs the command. <paramref name="args"/> is everything after the command name.</summary>
    void Execute(ISlashCommandHost host, string args);

    /// <summary>
    /// Argument completions for the popup. <paramref name="partial"/> is the text typed after the
    /// command name. Returns an empty list when the command has no argument completion.
    /// </summary>
    IReadOnlyList<CompletionItem> Complete(ISlashCommandHost host, string partial) => [];
}
