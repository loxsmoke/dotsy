using Dotsy.Core.Loop.Data;
using Dotsy.Core.Providers;
using System.Text;

namespace Dotsy.Core.Retrieval;

public static class RepoMap
{
    public static string? Build(
        string cwd,
        int tokenBudget,
        LoopContext ctx)
    {
        if (tokenBudget <= 0)
            return null;

        using var index = new RoslynIndex(Path.Combine(cwd, ".dotsy", "cache"));
        index.Open();
        var outlines = index.ScanDirectory(cwd);
        var mentionedFiles = GetRepoMapPersonalizationInputs(ctx);

        if (outlines.Count == 0)
            return null;

        var scores = ComputePageRank(outlines, mentionedFiles);

        // Sort by score descending
        var ranked = outlines
            .Select((o, i) => (outline: o, score: scores[i] * NoiseWeight(o)))
            .OrderByDescending(x => x.score)
            .ToList();

        const int CharsPerToken = 4;
        var hasExplicitFileContext = mentionedFiles?.Count > 0;
        var effectiveTokenBudget = hasExplicitFileContext ? tokenBudget : tokenBudget * 8;
        int maxChars = effectiveTokenBudget * CharsPerToken;

        var sb = new StringBuilder();
        foreach (var (outline, _) in ranked)
        {
            if (sb.Length + outline.Outline.Length > maxChars)
                break;
            sb.Append(outline.Outline);
        }

        return sb.ToString().TrimEnd();
    }

    private static double[] ComputePageRank(
        IReadOnlyList<FileOutline> outlines,
        IReadOnlyList<string>? mentionedFiles,
        int iterations = 20,
        double damping = 0.85)
    {
        int n = outlines.Count;
        if (n == 0) return [];

        // Build name → indices map using type names from outlines. References are
        // bare type names, so two files sharing a name (e.g. Options.cs in different
        // directories) must both be reachable — map to a list, not a single index,
        // so neither is silently overwritten.
        var nameToIndices = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < n; i++)
        {
            var fn = Path.GetFileNameWithoutExtension(outlines[i].FilePath);
            if (!nameToIndices.TryGetValue(fn, out var list))
                nameToIndices[fn] = list = new List<int>();
            list.Add(i);
        }

        // Build adjacency: file i references file j
        var outEdges = new List<List<int>>();
        for (int i = 0; i < n; i++)
        {
            var edges = new List<int>();
            foreach (var refName in outlines[i].ReferencedFiles)
            {
                if (nameToIndices.TryGetValue(refName, out var targets))
                    foreach (var j in targets)
                        if (j != i)
                            edges.Add(j);
            }
            outEdges.Add(edges.Distinct().ToList());
        }

        // Personalization vector: boost mentioned files
        var personalization = new double[n];
        var mentioned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (mentionedFiles is not null)
            foreach (var f in mentionedFiles)
                mentioned.Add(Path.GetFileNameWithoutExtension(f));

        for (int i = 0; i < n; i++)
        {
            var fn = Path.GetFileNameWithoutExtension(outlines[i].FilePath);
            personalization[i] = mentioned.Contains(fn) ? 2.0 / n : 1.0 / n;
        }

        // Normalize personalization
        double pSum = personalization.Sum();
        for (int i = 0; i < n; i++) personalization[i] /= pSum;

        // Power iteration
        var scores = new double[n];
        for (int i = 0; i < n; i++) scores[i] = 1.0 / n;

        for (int iter = 0; iter < iterations; iter++)
        {
            var newScores = new double[n];

            // Add dangling node mass
            double dangling = 0;
            for (int i = 0; i < n; i++)
                if (outEdges[i].Count == 0)
                    dangling += scores[i];

            for (int j = 0; j < n; j++)
                newScores[j] += (1 - damping) * personalization[j] + damping * dangling * personalization[j];

            for (int i = 0; i < n; i++)
            {
                if (outEdges[i].Count == 0) continue;
                double share = damping * scores[i] / outEdges[i].Count;
                foreach (var j in outEdges[i])
                    newScores[j] += share;
            }

            double norm = newScores.Sum();
            if (norm > 0)
                for (int i = 0; i < n; i++) newScores[i] /= norm;

            scores = newScores;
        }

        return scores;
    }

    private static double NoiseWeight(FileOutline outline)
    {
        var weight = 1.0;
        var fileName = Path.GetFileName(outline.FilePath);
        if (IsGeneratedFile(fileName, outline.Outline))
            weight *= 0.25;

        if (IsPropertyHeavy(outline.Outline))
            weight *= 0.60;

        return weight;
    }

    private static bool IsGeneratedFile(string fileName, string outline) =>
        fileName.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase)
        || outline.Contains("<auto-generated", StringComparison.OrdinalIgnoreCase);

    private static bool IsPropertyHeavy(string outline)
    {
        var memberLines = outline.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("public ", StringComparison.Ordinal)
                || l.StartsWith("internal ", StringComparison.Ordinal)
                || l.StartsWith("protected ", StringComparison.Ordinal)
                || l.StartsWith("private ", StringComparison.Ordinal))
            .ToList();

        if (memberLines.Count < 4)
            return false;

        var propertyLines = memberLines.Count(l => !l.Contains('('));
        return propertyLines / (double)memberLines.Count >= 0.75;
    }

    private static IReadOnlyList<string> GetRepoMapPersonalizationInputs(LoopContext ctx)
    {
        var files = new HashSet<string>(ctx.AddedFiles, StringComparer.OrdinalIgnoreCase);

        var latestUserText = ctx.Messages
            .OfType<UserMessage>()
            .LastOrDefault()?
            .Content
            .OfType<TextBlock>()
            .Select(b => b.Text)
            .LastOrDefault();

        if (!string.IsNullOrWhiteSpace(latestUserText))
        {
            foreach (var file in ExtractMentionedFiles(latestUserText))
                files.Add(file);
        }

        return files.ToList();
    }

    private static IEnumerable<string> ExtractMentionedFiles(string text)
    {
        const string FileChars = @"[A-Za-z0-9_\-./\\]";
        var pattern = $@"(?<!{FileChars}){FileChars}+\.(?:cs|csproj|sln|slnx|props|targets|json|toml|md|txt|xml|yaml|yml)(?!{FileChars})";

        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
            text,
            pattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(100)))
        {
            yield return match.Value.Trim('\'', '"', '`', ',', '.', ':', ';', ')', ']', '}');
        }
    }
}
