using Dotsy.Core.Config;

namespace Dotsy.Core.Loop;

public sealed class SkillRecord
{
    public required string Name { get; init; }
    public required string Body { get; init; }
    public required string FilePath { get; init; }
    public required IReadOnlyList<string> CompanionPaths { get; init; }
}

public sealed class SkillDiscovery
{
    private readonly SkillsConfig _config;
    private readonly string _cwd;
    private readonly string _home;

    public SkillDiscovery(SkillsConfig config, string cwd)
        : this(config, cwd, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
    {
    }

    internal SkillDiscovery(SkillsConfig config, string cwd, string home)
    {
        _config = config;
        _cwd = cwd;
        _home = home;
    }

    public SkillRecord? Find(string name)
    {
        foreach (var path in GetSearchPaths())
        {
            if (!Directory.Exists(path))
                continue;

            foreach (var candidate in FindCandidates(path, name))
            {
                var body = File.ReadAllText(candidate);
                return new SkillRecord
                {
                    Name = SkillNameFromPath(candidate),
                    Body = body,
                    FilePath = candidate,
                    CompanionPaths = FindCompanions(candidate)
                };
            }
        }
        return null;
    }

    public IReadOnlyList<SkillRecord> FindAll()
    {
        var results = new List<SkillRecord>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in GetSearchPaths())
        {
            if (!Directory.Exists(path))
                continue;

            foreach (var file in EnumerateSkillFiles(path))
            {
                var skillName = SkillNameFromPath(file);
                if (!seen.Add(skillName))
                    continue;

                var body = File.ReadAllText(file);
                results.Add(new SkillRecord
                {
                    Name = skillName,
                    Body = body,
                    FilePath = file,
                    CompanionPaths = FindCompanions(file)
                });
            }
        }
        return results;
    }

    private IEnumerable<string> GetSearchPaths()
    {
        // Configured paths (absolute or relative to cwd)
        foreach (var p in _config.Paths)
            yield return Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(_cwd, p));

        foreach (var projectRoot in WalkUp(_cwd))
        {
            yield return Path.Combine(projectRoot, ".dotsy", "skills");
            yield return Path.Combine(projectRoot, ".agents", "skills");
        }

        yield return Path.Combine(_home, ".config", "dotsy", "skills");

        if (_config.CrossTool)
        {
            yield return Path.Combine(_home, ".agents", "skills");
            yield return Path.Combine(_home, ".claude", "skills");
        }
    }

    private static IReadOnlyList<string> FindCompanions(string skillFile)
    {
        var dir = Path.GetDirectoryName(skillFile) ?? "";
        if (Path.GetFileName(skillFile).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
        {
            return Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                .Where(f => !string.Equals(f, skillFile, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var baseName = Path.GetFileNameWithoutExtension(skillFile);
        var companions = new List<string>();

        foreach (var f in Directory.GetFiles(dir))
        {
            if (f == skillFile) continue;
            var fn = Path.GetFileNameWithoutExtension(f);
            if (fn.StartsWith(baseName, StringComparison.OrdinalIgnoreCase) ||
                fn.Equals(baseName, StringComparison.OrdinalIgnoreCase))
            {
                companions.Add(f);
            }
        }
        return companions;
    }

    private static IEnumerable<string> FindCandidates(string root, string name)
    {
        var flat = Path.Combine(root, name + ".md");
        if (File.Exists(flat))
            yield return flat;

        var dirSkill = Path.Combine(root, name, "SKILL.md");
        if (File.Exists(dirSkill))
            yield return dirSkill;

        foreach (var file in EnumerateSkillFiles(root)
            .Where(f => SkillNameFromPath(f).Equals(name, StringComparison.OrdinalIgnoreCase)))
            yield return file;
    }

    private static IEnumerable<string> EnumerateSkillFiles(string root)
    {
        foreach (var file in Directory.GetFiles(root, "*.md", SearchOption.TopDirectoryOnly))
            yield return file;

        foreach (var dir in Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            var skill = Path.Combine(dir, "SKILL.md");
            if (File.Exists(skill))
                yield return skill;
        }
    }

    private static string SkillNameFromPath(string file)
    {
        if (Path.GetFileName(file).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
            return new DirectoryInfo(Path.GetDirectoryName(file) ?? "").Name;
        return Path.GetFileNameWithoutExtension(file);
    }

    private static IEnumerable<string> WalkUp(string cwd)
    {
        var dir = Path.GetFullPath(cwd);
        while (!string.IsNullOrEmpty(dir))
        {
            yield return dir;
            dir = Directory.GetParent(dir)?.FullName ?? "";
        }
    }
}
