using Terminal.Gui;

namespace Dotsy.Cli.Tui;

internal sealed record ToolRow(
    string Name,
    string Arg,
    string Status,
    int Elapsed,
    DateTimeOffset StartedAt,
    string Cwd = "",
    List<List<Cell>>? Output = null,
    int Group = 0,
    string? Parameters = null)
{
    public override string ToString()
    {
        var icon = Status switch { "OK" => "✓", "ERR" => "✗", "RUNNING" => "◌", _ => " " };
        var tail = Status switch {
            "RUNNING" => $" {Elapsed}s…",
            _ when Elapsed <= 1 => "",
            _ => $" {Elapsed}s"
        };
        // Pad the name to a column, but always keep at least one space before the argument so
        // long tool names don't run straight into the argument text.
        var name = Name.Length >= 10 ? Name + " " : $"{Name,-10}";
        return $" {icon}  {name}{Arg,-28}{tail}";
    }
}
