using Dotsy.Cli.SlashCommands.Interfaces;
using Dotsy.Cli.Tui;

namespace Dotsy.Cli.SlashCommands;

/// <summary>
/// <c>/add &lt;path&gt;</c> — adds a file path to the current loop's read-only context. Also owns
/// its own filesystem path completion, which used to live in <see cref="AgentWindow"/>.
/// </summary>
internal sealed class AddCommand : ISlashCommand
{
    public string Name => "add";

    public IReadOnlyList<SlashCommandUsage> Usages =>
    [
        new("/add <path>", "Add a file path to read-only context for the current loop."),
    ];

    public void Execute(ISlashCommandHost host, string args)
    {
        if (string.IsNullOrEmpty(args))
        {
            host.Write("usage: /add <path>\n\n", Palette.Warn);
            return;
        }

        var abs = Path.IsPathRooted(args)
            ? args
            : Path.GetFullPath(Path.Combine(TuiSessionContext.Cwd, args));
        var ctx = TuiSessionContext.LoopCtx;
        if (ctx is not null && !ctx.AddedFiles.Contains(abs))
        {
            ctx.AddedFiles.Add(abs);
            host.Write($"added: {args}\n\n", Palette.Success);
        }
        else
        {
            host.Write($"already in context: {args}\n\n", Palette.Dim);
        }
    }

    public IReadOnlyList<CompletionItem> Complete(ISlashCommandHost host, string partial)
    {
        var cwd = TuiSessionContext.Cwd;
        var normalized = partial.TrimStart();
        var rooted = Path.IsPathRooted(normalized);
        var endsWithSeparator = normalized.EndsWith(Path.DirectorySeparatorChar)
            || normalized.EndsWith(Path.AltDirectorySeparatorChar);

        string searchDir;
        string prefix;
        string typedDir;

        if (endsWithSeparator)
        {
            typedDir = normalized;
            searchDir = rooted ? normalized : Path.GetFullPath(Path.Combine(cwd, normalized));
            prefix = "";
        }
        else
        {
            typedDir = Path.GetDirectoryName(normalized) ?? "";
            prefix = Path.GetFileName(normalized);
            searchDir = string.IsNullOrEmpty(typedDir)
                ? cwd
                : rooted
                    ? typedDir
                    : Path.GetFullPath(Path.Combine(cwd, typedDir));
        }

        if (!Directory.Exists(searchDir))
            return [];

        static bool StartsWithPrefix(string name, string prefix) =>
            string.IsNullOrEmpty(prefix)
            || name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

        string BuildReplacement(string name, bool isDir)
        {
            var completed = string.IsNullOrEmpty(typedDir)
                ? name
                : Path.Combine(typedDir, name);
            if (isDir && !completed.EndsWith(Path.DirectorySeparatorChar))
                completed += Path.DirectorySeparatorChar;
            return "/add " + completed;
        }

        var dirs = Directory.EnumerateDirectories(searchDir)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n) && StartsWithPrefix(n!, prefix))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(n => new CompletionItem(n! + Path.DirectorySeparatorChar, BuildReplacement(n!, isDir: true)));

        var files = Directory.EnumerateFiles(searchDir)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n) && StartsWithPrefix(n!, prefix))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(n => new CompletionItem(n!, BuildReplacement(n!, isDir: false)));

        return dirs.Concat(files).Take(12).ToList();
    }
}
