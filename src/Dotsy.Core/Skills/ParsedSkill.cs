namespace Dotsy.Core.Skills;

public sealed class ParsedSkill
{
    public required SkillFrontmatter Frontmatter { get; init; }
    public required string Body { get; init; }
    public required string FilePath { get; init; }
    public required IReadOnlyList<string> CompanionPaths { get; init; }
}
