using Dotsy.Core.Config;
using Dotsy.Core.Utils;

namespace Dotsy.Core.Skills;

/// <summary>
/// Discovers skill definitions (Markdown files) from a prioritised set of well-known locations.
///
/// Search order (first match wins for <see cref="Find"/>; all are merged for <see cref="FindAll"/>):
/// <list type="number">
///   <item>Paths listed in <see cref="Config.SkillsConfig.Paths"/> (absolute, or relative to the current working directory).</item>
///   <item><c>.dotsy/skills</c> and <c>.agents/skills</c> inside every ancestor directory, walking up from the current working directory.</item>
///   <item><c>~/.config/dotsy/skills</c> (user-level dotsy config).</item>
///   <item>When <see cref="Config.SkillsConfig.CrossTool"/> is enabled: <c>~/.agents/skills</c> and <c>~/.claude/skills</c>.</item>
/// </list>
///
/// Within each search path a skill can be stored in two layouts:
/// <list type="bullet">
///   <item>A flat Markdown file — <c>{skills-dir}/{skill-name}.md</c></item>
///   <item>A named sub-directory containing a <c>SKILL.md</c> entry point — <c>{skills-dir}/{skill-name}/SKILL.md</c>; all other files in that directory are treated as companions.</item>
/// </list>
///
/// Skill names are derived from the file path: the parent directory name for <c>SKILL.md</c> files, or the filename stem for flat <c>.md</c> files.
/// When <see cref="FindAll"/> is called, duplicate names are suppressed and the first occurrence (by search order) is kept.
/// </summary>
public sealed class SkillDiscovery
{
    public const string DefaultSkillFileName = "SKILL.md";
    private readonly SkillsConfig skillsConfig;
    private readonly string workingDirectory;
    private readonly string homeDirectory;

    public SkillDiscovery(SkillsConfig config, string cwd)
        : this(config, cwd, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
    {
    }

    internal SkillDiscovery(SkillsConfig config, string cwd, string home)
    {
        skillsConfig = config;
        workingDirectory = cwd;
        homeDirectory = home;
    }

    public SkillRecord? Find(string name)
    {
        foreach (var path in GetSearchPaths())
        {
            if (!Directory.Exists(path))
                continue;

            foreach (var skillFileName in FindCandidates(path, name))
            {
                var body = File.ReadAllText(skillFileName);
                return new SkillRecord
                {
                    Name = SkillNameFromPath(skillFileName),
                    Body = body,
                    FilePath = skillFileName,
                    CompanionPaths = FindCompanions(skillFileName)
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

            foreach (var skillFileName in EnumerateSkillFiles(path))
            {
                var skillName = SkillNameFromPath(skillFileName);
                if (!seen.Add(skillName))
                    continue;

                var body = File.ReadAllText(skillFileName);
                results.Add(new SkillRecord
                {
                    Name = skillName,
                    Body = body,
                    FilePath = skillFileName,
                    CompanionPaths = FindCompanions(skillFileName)
                });
            }
        }
        return results;
    }

    private IEnumerable<string> GetSearchPaths()
    {
        // Configured paths (absolute or relative to cwd)
        foreach (var p in skillsConfig.Paths)
            yield return Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(workingDirectory, p));

        foreach (var projectRoot in WalkUp(workingDirectory))
        {
            yield return Path.Combine(projectRoot, ".dotsy", "skills");
            yield return Path.Combine(projectRoot, ".agents", "skills");
        }

        yield return Path.Combine(homeDirectory, ".config", "dotsy", "skills");

        if (skillsConfig.CrossTool)
        {
            yield return Path.Combine(homeDirectory, ".agents", "skills");
            yield return Path.Combine(homeDirectory, ".claude", "skills");
        }
    }

    /// <summary>
    /// Returns the companion file paths associated with the given skill file.
    /// For sub-directory skills (<c>SKILL.md</c>), all other files inside that directory are returned recursively.
    /// For flat <c>.md</c> skills, files in the same directory whose name starts with the skill's base name are returned.
    /// Results are returned in case-insensitive alphabetical order.
    /// </summary>
    /// <param name="skillFile">The absolute path of the skill's entry-point file.</param>
    /// <returns>
    /// A read-only list of absolute companion file paths, or an empty list when no companions exist.
    /// </returns>
    private static IReadOnlyList<string> FindCompanions(string skillFile)
    {
        var dir = Path.GetDirectoryName(skillFile) ?? "";
        if (Path.GetFileName(skillFile).EqualsNoCase(DefaultSkillFileName))
        {
            return Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                .Where(f => !f.EqualsNoCase(skillFile))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var baseName = Path.GetFileNameWithoutExtension(skillFile);
        var companions = new List<string>();

        foreach (var f in Directory.GetFiles(dir))
        {
            if (f == skillFile) continue;
            var fn = Path.GetFileNameWithoutExtension(f);
            if (fn.StartsWithNoCase(baseName) ||
                fn.EqualsNoCase(baseName))
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

        var dirSkill = Path.Combine(root, name, DefaultSkillFileName);
        if (File.Exists(dirSkill))
            yield return dirSkill;

        foreach (var file in EnumerateSkillFiles(root)
            .Where(f => SkillNameFromPath(f).EqualsNoCase(name)))
            yield return file;
    }

    private static IEnumerable<string> EnumerateSkillFiles(string root)
    {
        foreach (var file in Directory.GetFiles(root, "*.md", SearchOption.TopDirectoryOnly))
            yield return file;

        foreach (var dir in Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            var skill = Path.Combine(dir, DefaultSkillFileName);
            if (File.Exists(skill))
                yield return skill;
        }
    }

    /// <summary>
    /// Convert the file name to the skill name.
    /// Example 1: home/my-skill/SKILL.md  => my-skill
    /// Example 2: home/my-skill/true-skill.md  => true-skill
    /// </summary>
    /// <param name="file">The file name to the skill file</param>
    /// <returns>The skill name</returns>
    private static string SkillNameFromPath(string file)
    {
        if (Path.GetFileName(file).EqualsNoCase(DefaultSkillFileName))
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
