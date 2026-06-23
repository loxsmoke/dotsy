using Dotsy.Core.Config;
using Dotsy.Core.Loop;
using Dotsy.Core.Loop.Data;
using Dotsy.Core.Skills;

namespace Dotsy.Core.Tests;

[TestClass]
public sealed class SystemPromptBuilderTests
{
    [TestMethod]
    public void Build_IncludesLoadedSkills()
    {
        var config = DefaultConfig.Create();
        config.Agent.InjectEnvironment = false;
        config.Retrieval.RepoMapTokens = 0;
        var ctx = new LoopContext();
        ctx.LoadedSkills["helper"] = "Use the helper skill.";

        var prompt = SystemPromptBuilder.Build(config, Environment.CurrentDirectory, ctx);

        StringAssert.Contains(prompt, "<loaded_skills>");
        StringAssert.Contains(prompt, "<skill_content name=\"helper\">");
        StringAssert.Contains(prompt, "Use the helper skill.");
    }

    [TestMethod]
    public async Task Build_IncludesAddedFilesAsReadOnlyContext()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"dotsy_prompt_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tmp, "notes.txt"), "important context\n");
            var config = DefaultConfig.Create();
            config.Agent.InjectEnvironment = false;
            config.Retrieval.RepoMapTokens = 0;
            var ctx = new LoopContext();
            ctx.AddedFiles.Add("notes.txt");

            var prompt = SystemPromptBuilder.Build(config, tmp, ctx);

            StringAssert.Contains(prompt, "<added_files>");
            StringAssert.Contains(prompt, "Files added via /add are read-only context");
            StringAssert.Contains(prompt, "<file path=\"notes.txt\">");
            StringAssert.Contains(prompt, "important context");
        }
        finally
        {
            if (Directory.Exists(tmp))
                Directory.Delete(tmp, recursive: true);
        }
    }

    [TestMethod]
    public async Task Build_InjectsProjectContextFromAgentsMd()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"dotsy_prompt_agents_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(tmp, "AGENTS.md"),
                "The solution file is Dotsy.slnx (not .sln).\n");
            var config = DefaultConfig.Create();
            config.Agent.InjectEnvironment = false;
            config.Retrieval.RepoMapTokens = 0;

            var prompt = SystemPromptBuilder.Build(config, tmp, new LoopContext());

            StringAssert.Contains(prompt, "<project_context>");
            StringAssert.Contains(prompt, "from AGENTS.md");
            StringAssert.Contains(prompt, "The solution file is Dotsy.slnx (not .sln).");
        }
        finally
        {
            if (Directory.Exists(tmp))
                Directory.Delete(tmp, recursive: true);
        }
    }

    [TestMethod]
    public void Build_OmitsProjectContextWhenNoAgentsMd()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"dotsy_prompt_noagents_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var config = DefaultConfig.Create();
            config.Agent.InjectEnvironment = false;
            config.Retrieval.RepoMapTokens = 0;

            var prompt = SystemPromptBuilder.Build(config, tmp, new LoopContext());

            Assert.IsFalse(prompt.Contains("<project_context>", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(tmp))
                Directory.Delete(tmp, recursive: true);
        }
    }

    [TestMethod]
    public async Task Build_UsesSkillDescriptionsAndOmitsModelDisabledSkills()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"dotsy_prompt_skills_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var skillDir = Path.Combine(tmp, ".dotsy", "skills");
            Directory.CreateDirectory(skillDir);
            await File.WriteAllTextAsync(Path.Combine(skillDir, "visible.md"), """
                ---
                name: visible
                description: Frontmatter description.
                ---
                Body first line.
                """);
            await File.WriteAllTextAsync(Path.Combine(skillDir, "manual.md"), """
                ---
                name: manual
                description: Hidden description.
                disable-model-invocation: true
                ---
                Hidden body.
                """);

            var config = DefaultConfig.Create();
            config.Agent.InjectEnvironment = false;
            config.Retrieval.RepoMapTokens = 0;

            var prompt = SystemPromptBuilder.Build(
                config,
                tmp,
                new LoopContext(),
                skillDiscovery: new SkillDiscovery(config.Skills, tmp));

            StringAssert.Contains(prompt, "<available_skills>");
            StringAssert.Contains(prompt, "visible: Frontmatter description.");
            Assert.IsFalse(prompt.Contains("manual:", StringComparison.Ordinal));
            Assert.IsFalse(prompt.Contains("Hidden description", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(tmp))
                Directory.Delete(tmp, recursive: true);
        }
    }
}
