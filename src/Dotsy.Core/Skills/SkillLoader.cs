using Dotsy.Core.Utils;

namespace Dotsy.Core.Skills;

public static class SkillLoader
{
    public static ParsedSkill Load(SkillRecord record)
    {
        var (frontmatter, body) = ParseFrontmatter(record.Body);
        if (string.IsNullOrEmpty(frontmatter.Name))
            frontmatter.Name = record.Name;

        return new ParsedSkill
        {
            Frontmatter = frontmatter,
            Body = body,
            FilePath = record.FilePath,
            CompanionPaths = record.CompanionPaths
        };
    }

    private static (SkillFrontmatter, string body) ParseFrontmatter(string content)
    {
        var fm = new SkillFrontmatter();
        if (!content.StartsWith("---"))
            return (fm, content);

        int end = content.IndexOf("---", 3);
        if (end == -1)
            return (fm, content);

        var yamlBlock = content[3..end].Trim();
        var body = content[(end + 3)..].TrimStart('\n', '\r');

        // Simple key: value YAML parser (no nested, no arrays except inline)
        foreach (var rawLine in yamlBlock.Split('\n'))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#')) continue;

            var colonIdx = line.IndexOf(':');
            if (colonIdx == -1) continue;

            var key = line[..colonIdx].Trim().ToLowerInvariant();
            var value = line[(colonIdx + 1)..].Trim().Trim('"', '\'');

            switch (key)
            {
                case "name": fm.Name = value; break;
                case "description": fm.Description = value; break;
                case "disable-model-invocation":
                    fm.DisableModelInvocation = value.EqualsNoCase("true");
                    break;
                case "allowed-tools":
                    // Handle inline list: [Tool1, Tool2] or comma-separated
                    var tools = value.Trim('[', ']').Split(',');
                    fm.AllowedTools = [.. tools.Select(t => t.Trim()).Where(t => t.Length > 0)];
                    break;
            }
        }

        return (fm, body);
    }
}
