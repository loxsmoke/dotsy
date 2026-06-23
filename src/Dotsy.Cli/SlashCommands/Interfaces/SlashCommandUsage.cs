namespace Dotsy.Cli.SlashCommands.Interfaces;

/// <summary>
/// A single usage line shown in <c>/help</c>. A command may expose several
/// (e.g. <c>/config</c>, <c>/config list</c>, <c>/config &lt;key&gt; &lt;value&gt;</c>).
/// </summary>
public sealed record SlashCommandUsage(string Syntax, string Description);
