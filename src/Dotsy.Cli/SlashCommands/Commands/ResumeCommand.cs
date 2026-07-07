using Dotsy.Cli.SlashCommands.Interfaces;
using Dotsy.Cli.Tui;
using Dotsy.Cli.Tui.Colors;
using Dotsy.Core.Loop.Data;
using Dotsy.Core.Providers;
using Dotsy.Core.Session;
using System.Text.Json;

namespace Dotsy.Cli.SlashCommands;

/// <summary>
/// <c>/resume</c> — restores a previously saved session (most recent, or a specific id).
/// </summary>
internal sealed class ResumeCommand : ISlashCommand
{
    private const int ListLimit = 5;

    public string Name => "resume";

    public bool RequiresIdle => true;

    public IReadOnlyList<SlashCommandUsage> Usages =>
    [
        new("/resume", "Resume the most recent saved session for the current working directory."),
        new("/resume <id>", "Resume a specific saved session ID."),
        new("/resume list", "List the five most recent saved sessions for the current working directory."),
    ];

    public void Execute(ISlashCommandHost host, string args)
    {
        var config = TuiSessionContext.Config;
        var cwd = TuiSessionContext.Cwd;
        var sessionDir = SessionStore.ResolveDir(config.Session.LogDir, cwd);
        if (string.Equals(args.Trim(), "list", StringComparison.OrdinalIgnoreCase))
        {
            ListRecentSessions(host, sessionDir, cwd);
            return;
        }

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

        // Restore the token budget. UsedTokens comes back from the saved session immediately so the
        // context gauge reflects the real figure (not 0%) right after resume; ContextWindow is sized
        // to the active model below, asynchronously, since that needs a provider round-trip.
        loopCtx.TokenBudget = new TokenBudget(
            TokenBudget.Empty.ContextWindow,
            config.Compaction.ReserveTokens,
            config.Compaction.KeepRecentTokens,
            loaded.UsedTokens,
            config.Compaction.ThresholdPct);

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

        host.SetSession(loaded.SessionId);
        host.UpdateStatusBarFromCtx();
        host.RenderLoadedSession(loaded);

        // Size the context window to the active model. The lookup awaits an HTTP call (its
        // continuation posts back to the UI loop), so blocking on it here would deadlock the UI
        // thread — run it off-thread and fold the result into the restored budget when it resolves.
        var lookup = TuiSessionContext.ModelInfoLookup;
        var activeModelId = config.Model.ActiveModel.Id;
        _ = Task.Run(async () =>
        {
            ModelInfo? info = null;
            try
            {
                if (lookup is not null)
                    info = await lookup(activeModelId).ConfigureAwait(false);
            }
            catch { /* leave the default window in place */ }

            if (info is null) return;

            host.Invoke(() =>
            {
                if (TuiSessionContext.LoopCtx is not { } ctx) return;
                ctx.TokenBudget = ctx.TokenBudget with { ContextWindow = info.ContextWindow };
                host.UpdateStatusBarFromCtx();
            });
        });
    }

    public IReadOnlyList<CompletionItem> Complete(ISlashCommandHost host, string partial) =>
        BuildCompletions(partial);

    private static IReadOnlyList<CompletionItem> BuildCompletions(string partial)
    {
        var text = partial.TrimStart();
        if (string.IsNullOrEmpty(text))
        {
            return
            [
                new("list", "/resume list"),
                new("select session", "/resume select session "),
            ];
        }

        if ("list".StartsWith(text, StringComparison.OrdinalIgnoreCase))
            return [new CompletionItem("list", "/resume list")];

        const string selectSession = "select session";
        if (!selectSession.StartsWith(text, StringComparison.OrdinalIgnoreCase) &&
            !text.StartsWith(selectSession + " ", StringComparison.OrdinalIgnoreCase))
            return [];

        if (!text.StartsWith(selectSession + " ", StringComparison.OrdinalIgnoreCase))
            return [new CompletionItem("select session", "/resume select session ")];

        var rest = text[selectSession.Length..].TrimStart();
        var sessions = GetSessionsForCurrentCwd();
        if (string.IsNullOrEmpty(rest))
            return DayCompletions(sessions, "");

        var trailingSpace = text.EndsWith(' ');
        var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return DayCompletions(sessions, "");

        var day = parts[0];
        if (!trailingSpace)
            return DayCompletions(sessions, day);

        return sessions
            .Where(s => SessionDay(s).Equals(day, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.UpdatedAt)
            .Select(s => new CompletionItem(SessionDisplay(s), "/resume " + s.SessionId))
            .ToList();
    }

    private static IReadOnlyList<Dotsy.Core.Session.Data.SessionIndexEntry> GetSessionsForCurrentCwd()
    {
        var sessionDir = SessionStore.ResolveDir(TuiSessionContext.Config.Session.LogDir, TuiSessionContext.Cwd);
        return SessionStore.GetAllSessions(sessionDir, TuiSessionContext.Cwd);
    }

    private static IReadOnlyList<CompletionItem> DayCompletions(
        IReadOnlyList<Dotsy.Core.Session.Data.SessionIndexEntry> sessions,
        string prefix) =>
        sessions
            .Select(SessionDay)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(day => day.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(day => day, StringComparer.OrdinalIgnoreCase)
            .Select(day => new CompletionItem(day, "/resume select session " + day + " "))
            .ToList();

    private static string SessionDay(Dotsy.Core.Session.Data.SessionIndexEntry session) =>
        session.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd");

    private static string SessionDisplay(Dotsy.Core.Session.Data.SessionIndexEntry session) =>
        string.IsNullOrWhiteSpace(session.Title)
            ? session.SessionId
            : $"{session.SessionId}  {SingleLine(session.Title)}";

    private static void ListRecentSessions(ISlashCommandHost host, string sessionDir, string cwd)
    {
        var sessions = SessionStore.GetAllSessions(sessionDir, cwd);
        var shown = sessions.Take(ListLimit).ToList();

        host.Write($"Recent sessions: showing {shown.Count} of {sessions.Count}\n", Palette.Bright);
        if (shown.Count == 0)
        {
            host.Write("no sessions found for current working directory\n\n", Palette.Warn);
            return;
        }
        host.Write("ID          Ago       Steps Model              Title\n", Palette.Dim);
        host.Write("----------  --------  ----- ------------------ ------------------------------\n", Palette.Dim);

        var now = DateTimeOffset.UtcNow;
        foreach (var session in shown)
        {
            var details = ReadSessionListDetails(sessionDir, session.SessionId);
            var lastCommandAt = details.LastUserTimestamp ?? session.UpdatedAt;
            var stepCount = details.StepCount ?? session.MessageCount;
            var title = string.IsNullOrWhiteSpace(session.Title) ? "(untitled)" : session.Title;
            var row =
                $"{session.SessionId,-10}  " +
                $"{FormatAgo(lastCommandAt, now),-8}  " +
                $"{stepCount,5} " +
                $"{Truncate(session.Model, 18),-18} " +
                $"{Truncate(SingleLine(title), 30)}\n";
            host.Write(row, Palette.Normal);
        }

        host.Write("\n", Palette.Normal);
    }

    private static SessionListDetails ReadSessionListDetails(string sessionDir, string sessionId)
    {
        var path = Path.Combine(sessionDir, $"{sessionId}.jsonl");
        if (!File.Exists(path))
            return new SessionListDetails(null, null);

        var steps = 0;
        DateTimeOffset? lastUserTimestamp = null;
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var type) ||
                    !string.Equals(type.GetString(), "user", StringComparison.OrdinalIgnoreCase))
                    continue;

                steps++;
                if (root.TryGetProperty("timestamp", out var timestamp) &&
                    timestamp.ValueKind == JsonValueKind.String &&
                    DateTimeOffset.TryParse(timestamp.GetString(), out var parsed))
                    lastUserTimestamp = parsed;
            }
            catch
            {
                // Ignore partial/corrupt records; the index still provides enough list data.
            }
        }

        return new SessionListDetails(steps, lastUserTimestamp);
    }

    private static string FormatAgo(DateTimeOffset timestamp, DateTimeOffset now)
    {
        var elapsed = now - timestamp.ToUniversalTime();
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;

        if (elapsed.TotalMinutes < 1) return "now";
        if (elapsed.TotalHours < 1) return $"{(int)elapsed.TotalMinutes}m ago";
        if (elapsed.TotalDays < 1) return $"{(int)elapsed.TotalHours}h ago";
        return $"{(int)elapsed.TotalDays}d ago";
    }

    private static string SingleLine(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..Math.Max(0, maxLength - 3)] + "...";

    private sealed record SessionListDetails(int? StepCount, DateTimeOffset? LastUserTimestamp);
}
