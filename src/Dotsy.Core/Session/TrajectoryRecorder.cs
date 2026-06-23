using Dotsy.Core.Config;
using Dotsy.Core.Loop.Data;
using Dotsy.Core.Providers;
using Dotsy.Core.Session.Data;

namespace Dotsy.Core.Session;

public sealed class TrajectoryRecorder
{
    private readonly DotsyConfig _config;
    private readonly string _cwd;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private ChatRequest? _initialRequest;

    public TrajectoryTokenUsage TokenUsage { get; } = new();

    public TrajectoryRecorder(DotsyConfig config, string cwd)
    {
        _config = config;
        _cwd = cwd;
    }

    public void CaptureInitialRequest(ChatRequest request)
    {
        _initialRequest ??= request;
    }

    public void RecordUsage(int inputTokens, int outputTokens, int cacheReadTokens, int cacheWriteTokens)
    {
        TokenUsage.InputTokens += inputTokens;
        TokenUsage.OutputTokens += outputTokens;
        TokenUsage.CacheReadTokens += cacheReadTokens;
        TokenUsage.CacheWriteTokens += cacheWriteTokens;
    }

    public void Export(LoopContext ctx, EndReason reason, string? error = null)
    {
        if (!_config.Trajectory.Enabled || _initialRequest is null)
            return;

        var endedAt = DateTimeOffset.UtcNow;
        var doc = new TrajectoryDocument
        {
            Question = FirstUserQuestion(ctx),
            AgentPrompt = _initialRequest.SystemPrompt,
            EnabledTools = [.. _initialRequest.Tools.Select(t => t.Name)],
            SkillsPath = string.Join(Path.PathSeparator, _config.Skills.Paths),
            Uuid = ctx.SessionId,
            Messages = TrajectoryConverter.ToOpenAiMessages(_initialRequest.SystemPrompt, ctx),
            Tools = TrajectoryConverter.ToToolRows(_initialRequest.Tools),
            Metadata = new TrajectoryMetadata
            {
                Uuid = ctx.SessionId,
                Cwd = _cwd,
                GitBranch = TryGetGitBranch(_cwd),
                GitCommit = TryGetGitCommit(_cwd),
                Model = _config.Model.ActiveModelId,
                Provider = _config.Model.Provider,
                StartedAt = _startedAt,
                EndedAt = endedAt,
                DurationMs = (long)(endedAt - _startedAt).TotalMilliseconds,
                TokenUsage = TokenUsage,
                Outcome = Outcome(reason),
                Error = error
            }
        };

        var redacted = TrajectoryRedactor.Redact(doc, _config);
        var dir = ResolveDir(_config.Trajectory.Dir, _cwd);
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
        EndReason.TurnLimitReached => "turn_limit",
        EndReason.NudgeLimitReached => "nudge_limit",
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
