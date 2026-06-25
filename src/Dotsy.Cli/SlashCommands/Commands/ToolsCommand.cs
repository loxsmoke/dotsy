using Dotsy.Cli.SlashCommands.Interfaces;
using Dotsy.Cli.Tui;
using Dotsy.Cli.Tui.Colors;

namespace Dotsy.Cli.SlashCommands;

/// <summary>
/// <c>/tools</c> — lists every tool definition currently registered and sent to the model.
/// </summary>
internal sealed class ToolsCommand : ISlashCommand
{
    public string Name => "tools";

    public IReadOnlyList<SlashCommandUsage> Usages =>
    [
        new("/tools", "List all tool definitions currently registered and sent to the LLM, including descriptions."),
    ];

    public void Execute(ISlashCommandHost host, string args)
    {
        var registry = TuiSessionContext.Registry;
        if (registry is null)
        {
            host.WriteError("tool registry not initialized");
            return;
        }

        var defs = registry.GetToolDefinitions();
        var maxColWidth = (defs.Any() ? defs.Max(t => t.Name.Length) : 0) + 1;
        host.Write($"  {defs.Count} tools registered\n\n", Palette.Dim);

        foreach (var t in defs.OrderBy(t => t.Name))
            host.WriteDescription(maxColWidth, t.Name, t.Description);
        host.Write("\n", Palette.Normal);
    }
}
