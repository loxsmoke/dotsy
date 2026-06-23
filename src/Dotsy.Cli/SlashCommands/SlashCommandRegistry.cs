using Dotsy.Cli.SlashCommands.Interfaces;

namespace Dotsy.Cli.SlashCommands;

/// <summary>
/// Resolves a slash-command name (or alias) to an <see cref="ISlashCommand"/>. Replaces the
/// hand-written dispatch <c>switch</c> in <see cref="AgentWindow"/>: a new command is registered
/// here once and carries its own metadata, execution and completion logic.
/// </summary>
internal sealed class SlashCommandRegistry
{
    private readonly Dictionary<string, ISlashCommand> commandsByName =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ISlashCommand> Commands { get; }

    /// <summary>Every command's usage lines, in registration order — drives /help and self-context.</summary>
    public IReadOnlyList<SlashCommandUsage> Usages { get; }

    /// <summary>All invocable names (primary names and aliases), sorted — drives name completion.</summary>
    public IReadOnlyList<string> Names { get; }

    public SlashCommandRegistry(IEnumerable<ISlashCommand> commands)
    {
        var list = commands.ToList();
        Commands = list;
        foreach (var command in list)
        {
            commandsByName[command.Name] = command;
            foreach (var alias in command.Aliases)
                commandsByName[alias] = command;
        }

        Usages = list.SelectMany(c => c.Usages).ToList();
        Names = commandsByName.Keys
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Looks up a command by name or alias; null when not registered.</summary>
    public ISlashCommand? Find(string name) =>
        commandsByName.TryGetValue(name, out var command) ? command : null;

    /// <summary>The full set of slash commands available in the TUI.</summary>
    public static SlashCommandRegistry CreateDefault() => new(
    [
        new HelpCommand(),
        new ClearCommand(),
        new ToolsCommand(),
        new CompactCommand(),
        new VerboseCommand(),
        new ConfigCommand(),
        new ModelCommand(),
        new ResumeCommand(),
        new SecCommand(),
        new SelfCommand(),
        new SkillCommand(),
        new AddCommand(),
        new UndoCommand(),
        new ExitCommand(),
    ]);
}
