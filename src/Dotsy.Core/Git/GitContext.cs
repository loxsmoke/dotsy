using Dotsy.Core.Git.Data;

namespace Dotsy.Core.Git;

public sealed class GitContext
{
    public string? Branch { get; set; }
    public string? ShortSha { get; set; }
    public int ModifiedCount { get; set; }
    public int UntrackedCount { get; set; }

    public static GitContext? TryLoad(string cwd)
    {
        try
        {
            using var repo = new LibGit2Sharp.Repository(
                LibGit2Sharp.Repository.Discover(cwd));

            var branch = repo.Head.FriendlyName;
            var sha = repo.Head.Tip?.Sha?[..7] ?? "";

            var status = repo.RetrieveStatus(new LibGit2Sharp.StatusOptions());
            int modified = status.Modified.Count() + status.Staged.Count();
            int untracked = status.Untracked.Count();

            return new GitContext
            {
                Branch = branch,
                ShortSha = sha,
                ModifiedCount = modified,
                UntrackedCount = untracked
            };
        }
        catch
        {
            return null;
        }
    }

    public static IReadOnlyList<GitChangedFile> GetChangedFiles(string cwd)
    {
        try
        {
            var repoPath = LibGit2Sharp.Repository.Discover(cwd);
            if (repoPath is null) return [];
            using var repo = new LibGit2Sharp.Repository(repoPath);
            var status = repo.RetrieveStatus(new LibGit2Sharp.StatusOptions());

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<GitChangedFile>();

            void Add(string path, bool isNew, bool isDeleted)
            {
                if (seen.Add(path)) result.Add(new(path, isNew, isDeleted));
            }

            foreach (var e in status.Added)      Add(e.FilePath, true, false);
            foreach (var e in status.Staged)     Add(e.FilePath, false, false);
            foreach (var e in status.Modified)   Add(e.FilePath, false, false);
            foreach (var e in status.Untracked)  Add(e.FilePath, true, false);
            foreach (var e in status.Missing)    Add(e.FilePath, false, true);
            foreach (var e in status.Removed)    Add(e.FilePath, false, true);

            return result;
        }
        catch { return []; }
    }
}
