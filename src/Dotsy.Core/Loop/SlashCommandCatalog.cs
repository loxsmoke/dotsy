namespace Dotsy.Core.Loop;

public sealed record SlashCommand(string Syntax, string Name, string Description);

public static class SlashCommandCatalog
{
    public static readonly IReadOnlyList<SlashCommand> Commands =
    [
        new("/help", "help", "Print the slash-command help text in the conversation panel."),
        new("/clear", "clear", "Clear the visible conversation panel, tool log, and changed-files panel for the current TUI view."),
        new("/tools", "tools", "List all tool definitions currently registered and sent to the LLM, including descriptions."),
        new("/compact", "compact", "Run manual conversation compaction for the current session."),
        new("/verbose", "verbose", "Toggle inline verbose mode for tool calls and tool results."),
        new("/config", "config", "Show the active config file path and current configuration values grouped by section."),
        new("/config list", "config", "List every settable configuration key, type, and description."),
        new("/config <key> <value>", "config", "Update a config value via ConfigEditor.Set."),
        new("/model", "model", "Show current provider, model ID, and API-key source."),
        new("/model <id>", "model", "Switch the in-memory model ID for the active session and update the status bar."),
        new("/resume", "resume", "Resume the most recent saved session for the current working directory."),
        new("/resume <id>", "resume", "Resume a specific saved session ID."),
        new("/sec", "sec", "Show a security summary of the tool permissions currently in effect."),
        new("/self", "self", "Ask the agent to summarize the current Dotsy runtime, configuration, environment, and command usage from a generated self-context prompt."),
        new("/self <question>", "self", "Ask a question about the current Dotsy runtime or usage with the generated self-context prompt injected as context."),
        new("/skill", "skill", "List discovered skills."),
        new("/skill <name>", "skill", "Load the named skill body into the current loop context immediately."),
        new("/add <path>", "add", "Add a file path to read-only context for the current loop."),
        new("/undo", "undo", "Reset tracked files to the previous git checkpoint for the current session turn, if one exists."),
        new("/exit", "exit", "Quit the TUI."),
        new("/quit", "quit", "Alias for /exit.")
    ];

    public static IReadOnlyList<string> Names { get; } =
        Commands.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c).ToArray();
}
