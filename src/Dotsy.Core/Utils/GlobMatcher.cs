using System.Text;
using System.Text.RegularExpressions;

namespace Dotsy.Core.Utils;

// Shared glob helpers. Tools accept simple globs ("*.cs", "**/*Foo*.cs") and models frequently
// pass a glob where a directory path was expected; centralising detection and matching here keeps
// every tool behaving the same way.
public static class GlobMatcher
{
    // Glob metacharacters that are invalid in a Windows path and so signal a glob, not a directory.
    public static bool LooksLikeGlob(string? value) =>
        !string.IsNullOrEmpty(value) && value.AsSpan().IndexOfAny('*', '?') >= 0;

    public static string Normalize(string pattern) =>
        pattern.Replace('\\', '/').TrimStart('/');

    // Builds a predicate testing a path against the glob. A pattern without a '/' matches the file
    // name only; otherwise it matches against the full (relative) path.
    public static Func<string, bool> Create(string pattern)
    {
        var normalized = Normalize(pattern);
        bool matchFileNameOnly = !normalized.Contains('/', StringComparison.Ordinal);
        var regex = new Regex("^" + GlobToRegex(normalized) + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return path =>
        {
            var candidate = path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
            if (matchFileNameOnly)
                candidate = Path.GetFileName(candidate);
            return regex.IsMatch(candidate);
        };
    }

    private static string GlobToRegex(string pattern)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < pattern.Length; i++)
        {
            var ch = pattern[i];
            if (ch == '*')
            {
                bool doubleStar = i + 1 < pattern.Length && pattern[i + 1] == '*';
                if (doubleStar)
                {
                    i++;
                    if (i + 1 < pattern.Length && pattern[i + 1] == '/')
                    {
                        i++;
                        sb.Append("(?:.*/)?");
                    }
                    else
                    {
                        sb.Append(".*");
                    }
                }
                else
                {
                    sb.Append("[^/]*");
                }
            }
            else if (ch == '?')
            {
                sb.Append("[^/]");
            }
            else
            {
                sb.Append(Regex.Escape(ch.ToString()));
            }
        }

        return sb.ToString();
    }
}
