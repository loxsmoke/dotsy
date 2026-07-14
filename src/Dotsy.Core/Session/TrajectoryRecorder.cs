using Dotsy.Core.Config;
using Dotsy.Core.Loop.Data;
using Dotsy.Core.Providers;
using Dotsy.Core.Session.Data;

namespace Dotsy.Core.Session;

public sealed class TrajectoryRecorder
{
    private readonly DotsyConfig config;
    private readonly string cwd;
    private readonly DateTimeOffset startedAt = DateTimeOffset.UtcNow;
    private ChatRequest? initialRequest;

    public TrajectoryTokenUsage TokenUsage { get; } = new();

    public TrajectoryRecorder(DotsyConfig config, string cwd)
    {
        this.config = config;
        this.cwd = cwd;
    }

    public void CaptureInitialRequest(ChatRequest request)
    {
        initialRequest ??= request;
    }

    public void RecordUsage(UsageUpdate usage, long observedDurationMs = 0)
    {
        TokenUsage.InputTokens += usage.InputTokens;
        TokenUsage.OutputTokens += usage.OutputTokens;
        TokenUsage.CacheReadTokens += usage.CacheReadTokens;
        TokenUsage.CacheWriteTokens += usage.CacheWriteTokens;
        TokenUsage.LlmDurationMs += usage.ServerDurationMs ?? observedDurationMs;
    }

    public void Export(LoopContext ctx, EndReason reason, string? error = null)
    {
        if (!config.Trajectory.Enabled || initialRequest is null)
            return;

        var endedAt = DateTimeOffset.UtcNow;
        var doc = new TrajectoryDocument
        {
            Question = FirstUserQuestion(ctx),
            AgentPrompt = initialRequest.SystemPrompt,
            EnabledTools = [.. initialRequest.Tools.Select(t => t.Name)],
            SkillsPath = string.Join(Path.PathSeparator, config.Skills.Paths),
            Uuid = ctx.SessionId,
            Messages = TrajectoryConverter.ToOpenAiMessages(initialRequest.SystemPrompt, ctx),
            Tools = TrajectoryConverter.ToToolRows(initialRequest.Tools),
            Metadata = new TrajectoryMetadata
            {
                Uuid = ctx.SessionId,
                Cwd = cwd,
                GitBranch = TryGetGitBranch(cwd),
                GitCommit = TryGetGitCommit(cwd),
                Model = config.Model.ActiveModel.Id,
                Provider = config.Model.Provider,
                StartedAt = startedAt,
                EndedAt = endedAt,
                DurationMs = (long)(endedAt - startedAt).TotalMilliseconds,
                TokenUsage = TokenUsage,
                Outcome = Outcome(reason),
                Error = error
            }
        };

        var redacted = TrajectoryRedactor.Redact(doc, config);
        var dir = ResolveDir(config.Trajectory.Dir, cwd);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{ctx.SessionId}.json");
        File.WriteAllText(path, redacted, System.Text.Encoding.UTF8);
    }

    private static string ResolveDir(string dir, string cwd) =>
        Path.IsPathRooted(dir) ? dir : Path.Combine(cwd, dir);

    private static string FirstUserQuestion(LoopContext ctx) =>
        ctx.Messages
            .OfType<UserMessage>()
            .SelectMany(m => m.Content)
            .OfType<TextBlock>()
            .Select(b => b.Text)
            .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? "";

    private static string Outcome(EndReason reason) => reason switch
    {
        EndReason.TaskComplete => "completed",
        EndReason.ResponseComplete => "completed",
        EndReason.TurnLimitReached => "turn_limit",
        EndReason.NudgeLimitReached => "nudge_limit",
        EndReason.NoProgress => "no_progress",
        EndReason.MaxTokens => "max_tokens",
        EndReason.Repetition => "repetition",
        EndReason.ToolErrorStreak => "tool_error_streak",
        EndReason.ContextTooSmall => "context_too_small",
        EndReason.Cancelled => "cancelled",
        EndReason.Error => "error",
        _ => reason.ToString()
    };

    private static string? TryGetGitBranch(string cwd)
    {
        try
        {
            var path = LibGit2Sharp.Repository.Discover(cwd);
            if (path is null) return null;
            using var repo = new LibGit2Sharp.Repository(path);
            return repo.Head.FriendlyName;
        }
        catch { return null; }
    }

    private static string? TryGetGitCommit(string cwd)
    {
        try
        {
            var path = LibGit2Sharp.Repository.Discover(cwd);
            if (path is null) return null;
            using var repo = new LibGit2Sharp.Repository(path);
            return repo.Head.Tip?.Sha;
        }
        catch { return null; }
    }
}
