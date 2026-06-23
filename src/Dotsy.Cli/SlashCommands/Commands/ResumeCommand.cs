using Dotsy.Cli.SlashCommands.Interfaces;
using Dotsy.Cli.Tui;
using Dotsy.Core.Loop.Data;
using Dotsy.Core.Providers;
using Dotsy.Core.Session;

namespace Dotsy.Cli.SlashCommands;

/// <summary>
/// <c>/resume</c> — restores a previously saved session (most recent, or a specific id).
/// </summary>
internal sealed class ResumeCommand : ISlashCommand
{
    public string Name => "resume";

    public bool RequiresIdle => true;

    public IReadOnlyList<SlashCommandUsage> Usages =>
    [
        new("/resume", "Resume the most recent saved session for the current working directory."),
        new("/resume <id>", "Resume a specific saved session ID."),
    ];

    public void Execute(ISlashCommandHost host, string args)
    {
        var config = TuiSessionContext.Config;
        var cwd = TuiSessionContext.Cwd;
        var sessionDir = SessionStore.ResolveDir(config.Session.LogDir, cwd);
        var loaded = string.IsNullOrWhiteSpace(args)
            ? SessionLoader.LoadMostRecent(sessionDir, cwd)
            : SessionLoader.Load(args.Trim(), sessionDir);

        if (loaded is null)
        {
            var target = string.IsNullOrWhiteSpace(args) ? "most recent session" : args.Trim();
            host.Write($"session not found: {target}\n\n", Palette.Warn);
            return;
        }

        var loopCtx = new LoopContext(loaded.SessionId);
        loopCtx.Messages.AddRange(loaded.Messages);

        // Seed prompt history with the user messages from the loaded session.
        foreach (var message in loaded.Messages)
        {
            if (message is UserMessage userMessage)
            {
                var textContent = string.Join(" ", userMessage.Content.OfType<TextBlock>().Select(t => t.Text));
                if (!string.IsNullOrEmpty(textContent))
                    host.AddPromptHistory(textContent);
            }
        }

        loopCtx.CompactionSummary = loaded.CompactionSummary;
        TuiSessionContext.LoopCtx = loopCtx;
        TuiSessionContext.Session = new SessionStore(
            loaded.SessionId,
            sessionDir,
            disabled: !config.Session.LogEnabled);
        if (TuiSessionContext.LoopFactory is { } factory)
            TuiSessionContext.Loop = factory();

        host.ResetToolAndFilePanels();
        host.SetSession(loaded.SessionId);
        host.UpdateStatusBarFromCtx();

        host.Write($"resumed session: {loaded.SessionId}\n", Palette.Success);
        host.Write($"messages loaded: {loaded.Messages.Count}\n\n", Palette.Dim);
    }
}
