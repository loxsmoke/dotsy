using System.Text;
using System.Text.Json;
using Dotsy.Core.Loop;
using static Dotsy.Core.Loop.SkillLoader;

using Dotsy.Core.Tools.Interfaces;

namespace Dotsy.Core.Tools;

public sealed class SkillTool : ITool
{
    public const string ToolName = "Skill";
    public string Name => ToolName;
    public string Description => "Look up a skill by name and return its content.";
    public JsonElement InputSchema => ToolSchemas.SkillSchema;
    public ToolSafety Safety => ToolSafety.ReadOnly;
    public bool IsCompletionSignal => false;
    private readonly SkillDiscovery _discovery;

    public string FormatPanelArgument(JsonElement input)
    {
        var name = input.GetProperty("name").GetString() ?? "";
        return name;
    }

    public string? FormatPanelResult(JsonElement result)
    {
        if (!result.TryGetProperty("output", out var output))
            return null;

        var text = output.GetString() ?? "";
        if (string.IsNullOrWhiteSpace(text))
            return null;

        // Extract skill name from XML
        var match = System.Text.RegularExpressions.Regex.Match(text, @"name=""([^""]+)""");
        if (!match.Success)
            return text;

        var skillName = match.Groups[1].Value;
        var lineCount = text.Count(c => c == '\n');

        return $"Skill '{skillName}' loaded ({lineCount} lines)";
    }

    public SkillTool(SkillDiscovery discovery)
    {
        _discovery = discovery;
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var name = input.GetProperty("name").GetString() ?? "";
        var record = _discovery.Find(name);

        if (record is null)
            return ToolResult.Err($"Skill not found: {name}");

        var skill = SkillLoader.Load(record);

        if (skill.Frontmatter.DisableModelInvocation)
            return ToolResult.Err($"Skill '{name}' requires explicit /skill command invocation");

        var skillName = skill.Frontmatter.Name;
        if (!ctx.LoopContext.LoadedSkills.ContainsKey(skillName))
        {
            if (ctx.EmitEvent is null)
                return ToolResult.Err($"Permission required to load skill '{skillName}'");

            var decision = new TaskCompletionSource<PermissionDecision>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            await ctx.EmitEvent(new PermissionRequired(ToolName, skillName, decision));

            if (await decision.Task.WaitAsync(ct) == PermissionDecision.Deny)
                return ToolResult.Err($"Permission denied: Skill({skillName})");
        }

        var sb = new StringBuilder();
        sb.AppendLine($"<skill_content name=\"{skillName}\" path=\"{skill.FilePath}\">");
        sb.AppendLine(skill.Body);

        if (skill.CompanionPaths.Count > 0)
        {
            sb.AppendLine("<companion_files>");
            foreach (var p in skill.CompanionPaths)
                sb.AppendLine($"  {p}");
            sb.AppendLine("</companion_files>");
        }

        sb.AppendLine("</skill_content>");

        ctx.LoopContext.LoadedSkills[skillName] = skill.Body;

        return ToolResult.Ok(sb.ToString());
    }
}
