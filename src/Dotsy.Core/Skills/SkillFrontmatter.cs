namespace Dotsy.Core.Skills;

public sealed class SkillFrontmatter
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> AllowedTools { get; set; } = [];
    public bool DisableModelInvocation { get; set; }
}
