using Dotsy.Cli.SlashCommands.Interfaces;
using Dotsy.Cli.Tui;
using Dotsy.Core.Skills;

namespace Dotsy.Cli.SlashCommands;

/// <summary>
/// <c>/skill</c> — lists discovered skills, or loads the named skill body into the loop context.
/// </summary>
internal sealed class SkillCommand : ISlashCommand
{
    public string Name => "skill";

    public IReadOnlyList<SlashCommandUsage> Usages =>
    [
        new("/skill", "List discovered skills."),
        new("/skill <name>", "Load the named skill body into the current loop context immediately."),
    ];

    public void Execute(ISlashCommandHost host, string args)
    {
        var disc = new SkillDiscovery(TuiSessionContext.Config.Skills, TuiSessionContext.Cwd);

        // List skills if no skill name is provided
        if (string.IsNullOrWhiteSpace(args))
        {
            var skills = disc.FindAll();
            if (skills.Count == 0)
            {
                host.Write("no skills found\n\n", Palette.Dim);
            }
            else
            {
                host.Write("skills:\n", Palette.Bright);
                foreach (var s in skills)
                    host.Write($"  {s.Name,-20}  {s.FilePath}\n", Palette.Normal);
                host.Write("\n", Palette.Normal);
            }
            return;
        }

        var record = disc.Find(args);
        var ctx = TuiSessionContext.LoopCtx;
        if (record is null)
        {
            host.Write($"skill not found: {args}\n\n", Palette.Warn);
        }
        else if (ctx is null)
        {
            host.WriteError("session context not initialized");
        }
        else
        {
            var skill = SkillLoader.Load(record);
            ctx.LoadedSkills[skill.Frontmatter.Name] = skill.Body;
            host.Write($"loaded skill: {skill.Frontmatter.Name}\n", Palette.Success);
            if (skill.CompanionPaths.Count > 0)
            {
                host.Write("companion files:\n", Palette.Dim);
                foreach (var p in skill.CompanionPaths)
                    host.Write($"  {p}\n", Palette.Dim);
            }
            host.Write("\n", Palette.Normal);
        }
    }
}
