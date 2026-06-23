using System.Text;
using LibGit2Sharp;

namespace Dotsy.Core.Git;

public sealed class GitIntegration
{
    private readonly string _cwd;

    public GitIntegration(string cwd)
    {
        _cwd = cwd;
    }

    public bool IsRepo => Repository.IsValid(Repository.Discover(_cwd) ?? "");

    /// <summary>Auto-stage files that were written/edited, then commit.</summary>
    public bool AutoCommit(string sessionId, int turn, string firstLine, IEnumerable<string>? affectedPaths = null)
    {
        try
        {
            using var repo = new Repository(Repository.Discover(_cwd));

            var paths = affectedPaths?.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (paths is { Count: > 0 })
            {
                foreach (var path in paths)
                    Commands.Stage(repo, ToRepoRelativePath(repo, path));
            }
            else
            {
                Commands.Stage(repo, "*");
            }

            if (!HasStagedChanges(repo))
                return false;

            var sig = BuildSignature(repo);
            var message = $"agent: {Truncate(firstLine, 72)}";
            repo.Commit(message, sig, sig);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Write a checkpoint ref for the current HEAD.</summary>
    public bool WriteCheckpoint(string sessionId, int turn)
    {
        try
        {
            using var repo = new Repository(Repository.Discover(_cwd));
            if (repo.Head.Tip is null) return false;

            var refName = $"refs/agent/checkpoints/{sessionId}/{turn}";
            repo.Refs.Add(refName, repo.Head.Tip.Id, allowOverwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Hard-reset working tree to the most recent checkpoint for this session.</summary>
    public bool Undo(string sessionId, int currentTurn)
    {
        try
        {
            using var repo = new Repository(Repository.Discover(_cwd));

            for (int t = currentTurn - 1; t >= 0; t--)
            {
                var refName = $"refs/agent/checkpoints/{sessionId}/{t}";
                var reference = repo.Refs[refName];
                if (reference is null) continue;

                var commit = repo.Lookup<Commit>(reference.TargetIdentifier);
                if (commit is null) continue;

                repo.Reset(ResetMode.Hard, commit);
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max];

    private static Signature BuildSignature(Repository repo)
    {
        var name = repo.Config.Get<string>("user.name")?.Value;
        var email = repo.Config.Get<string>("user.email")?.Value;

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(email))
            return new Signature("dotsy", "dotsy@localhost", DateTimeOffset.UtcNow);

        return new Signature(name, email, DateTimeOffset.UtcNow);
    }

    private static bool HasStagedChanges(Repository repo)
    {
        var status = repo.RetrieveStatus(new StatusOptions());
        return status.Any(e =>
            e.State.HasFlag(FileStatus.NewInIndex)
            || e.State.HasFlag(FileStatus.ModifiedInIndex)
            || e.State.HasFlag(FileStatus.DeletedFromIndex)
            || e.State.HasFlag(FileStatus.RenamedInIndex)
            || e.State.HasFlag(FileStatus.TypeChangeInIndex));
    }

    private static string ToRepoRelativePath(Repository repo, string path)
    {
        if (!Path.IsPathRooted(path))
            return path.Replace('\\', '/');

        var workDir = Path.GetFullPath(repo.Info.WorkingDirectory);
        var fullPath = Path.GetFullPath(path);
        return Path.GetRelativePath(workDir, fullPath).Replace('\\', '/');
    }
}
