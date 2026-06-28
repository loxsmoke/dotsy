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
        new("/verbose [true|false]", "Turn inline verbose mode on or off; omit the value to toggle it."),
    ];

    public void Execute(ISlashCommandHost host, string args)
    {
        var cfg = TuiSessionContext.Config;
        var value = args.Trim();
        if (value.Length == 0)
        {
            cfg.Tui.Verbose = !cfg.Tui.Verbose;
        }
        else if (bool.TryParse(value, out var enabled))
        {
            cfg.Tui.Verbose = enabled;
        }
        else
        {
            host.Write("  usage: /verbose [true|false]\n\n", Palette.Warn);
            return;
        }

        var state = cfg.Tui.Verbose ? "on" : "off";
        host.Write(
            $"  verbose {state}  (tool calls and results will{(cfg.Tui.Verbose ? "" : " not")} appear inline)\n\n",
            Palette.Dim);
    }

    public IReadOnlyList<CompletionItem> Complete(ISlashCommandHost host, string partial)
    {
        var prefix = partial.TrimStart();
        return new[] { "true", "false" }
            .Where(value => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(value => new CompletionItem(value, "/verbose " + value))
            .ToList();
    }
}
