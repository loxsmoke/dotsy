using System.Text;
using Dotsy.Core.Config;

namespace Dotsy.Core.Loop;

public static class SystemPromptBuilder
{
    private const int MaxAddedFileChars = 20_000;
    private const int MaxAddedFilesTotalChars = 60_000;

    public const string CompactionContinuationInstruction =
        "Context was summarised. Continue naturally based on the summary - do not mention that summarisation occurred.";

    public const string DefaultBase = """
        You are Dotsy, an AI coding agent. You help users with software engineering tasks.
        Your name is Dotsy. When the user refers to "Dotsy" (in any casing, e.g. "dotsy"), they are referring to you. Respond in the first person as Dotsy.
        You already know what you are. Answer questions about your own identity, purpose, or capabilities directly from this prompt and your own knowledge - do not search or read the codebase to describe yourself. Only investigate the code if the user asks about a specific implementation detail.

        - Be concise. Default to no comments in code unless the WHY is non-obvious.
        - Prefer editing existing files over creating new ones.
        - When searching for code, prefer targeted lookups over broad sweeps.
        - Always verify a plan before implementing it on complex tasks.

        Tool selection:
        - Prefer the built-in tools over the Shell tool. Use Glob to find, list, or count files by name/pattern; Grep to search file contents; Read to read a file; List to list a directory; FindDefs for code structure. Reach for Shell only when no other tool covers the action (building, running tests, git, running a program).
        - Shell commands are not portable across operating systems. Check the OS in the environment block before using platform-specific commands, and never assume Unix utilities (wc, grep, find, ls, cat) are present - use the built-in tools instead.

        Grounding and tool use:
        - Ground every factual claim about the user's project/codebase in tool output. If you did not verify something with a tool, do not assert it. (This does not apply to describing yourself - see above.)
        - For questions about what the app supports or how it is configured, consult configuration files and documentation, not only source code.
        - If a tool call fails or returns nothing, do not answer from guesswork. Read the error, correct the arguments (e.g. path, glob, exclude), and retry. Only report "not found" after a successful search returned no results.
        - Keep using tools until you have enough evidence to answer; a single failed or empty result is not a conclusion.
        - Once you have gathered enough evidence, respond with a text answer to the user. Do not repeat a tool call with the exact same arguments.
        """;

    public static string Build(
        DotsyConfig config,
        string cwd,
        LoopContext ctx,
        string? basePrompt = null,
        GitContext? git = null,
        SkillDiscovery? skillDiscovery = null,
        string? repoMap = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine(basePrompt ?? DefaultBase);

        // Environment block (section 20)
        var envBlock = EnvironmentBlock.Build(config, cwd, git);
        if (!string.IsNullOrEmpty(envBlock))
        {
            sb.AppendLine();
            sb.AppendLine(envBlock);
        }

        // Available skills block (section 21)
        if (skillDiscovery is not null)
        {
            var skills = skillDiscovery.FindAll()
                .Select(SkillLoader.Load)
                .Where(skill => !skill.Frontmatter.DisableModelInvocation)
                .ToList();
            if (skills.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("<available_skills>");
                foreach (var skill in skills)
                {
                    var desc = string.IsNullOrWhiteSpace(skill.Frontmatter.Description)
                        ? ExtractFirstLine(skill.Body)
                        : skill.Frontmatter.Description.Trim();
                    sb.AppendLine($"  {skill.Frontmatter.Name}: {desc}");
                }
                sb.AppendLine("</available_skills>");
            }
        }

        if (ctx.LoadedSkills.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("<loaded_skills>");
            foreach (var (name, body) in ctx.LoadedSkills.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine($"<skill_content name=\"{name}\">");
                sb.AppendLine(body.Trim());
                sb.AppendLine("</skill_content>");
            }
            sb.AppendLine("</loaded_skills>");
        }

        AppendAddedFiles(sb, cwd, ctx);

        // Repo map (section 24)
        if (!string.IsNullOrEmpty(repoMap) && config.Retrieval.RepoMapTokens > 0)
        {
            sb.AppendLine();
            sb.AppendLine("<repo_map>");
            sb.AppendLine(repoMap);
            sb.AppendLine("</repo_map>");
        }

        // Plan mode
        if (ctx.IsPlanMode)
        {
            sb.AppendLine();
            sb.AppendLine("<plan_mode>");
            sb.AppendLine("You are in plan mode. Produce a detailed step-by-step implementation plan.");
            sb.AppendLine("Do not write any code yet. When ready, present the plan and wait for approval.");
            sb.AppendLine("</plan_mode>");
        }

        // Prior context (after compaction)
        if (ctx.CompactionSummary is { Length: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("<prior_context>");
            sb.AppendLine(ctx.CompactionSummary);
            sb.AppendLine("</prior_context>");
            sb.AppendLine();
            sb.AppendLine(CompactionContinuationInstruction);
        }

        // Todo items
        if (ctx.TodoItems.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("<todo>");
            foreach (var item in ctx.TodoItems)
                sb.AppendLine($"- {item}");
            sb.AppendLine("</todo>");
        }

        return sb.ToString().TrimEnd();
    }

    private static string ExtractFirstLine(string markdown)
    {
        var lines = markdown.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var first = lines.FirstOrDefault(l => !l.TrimStart().StartsWith('#')) ?? "";
        return first.Trim();
    }

    private static void AppendAddedFiles(StringBuilder sb, string cwd, LoopContext ctx)
    {
        if (ctx.AddedFiles.Count == 0)
            return;

        var entries = new List<(string Path, string Content, bool Truncated)>();
        var usedChars = 0;

        foreach (var addedPath in ctx.AddedFiles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var resolved = ResolvePath(cwd, addedPath);
            var displayPath = MakeRelative(resolved, cwd);

            if (!File.Exists(resolved))
            {
                entries.Add((displayPath, $"<missing: {resolved}>", false));
                continue;
            }

            if (IsBinaryFile(resolved))
            {
                entries.Add((displayPath, $"<binary file omitted: {resolved}>", false));
                continue;
            }

            string content;
            try
            {
                content = File.ReadAllText(resolved);
            }
            catch (Exception ex)
            {
                entries.Add((displayPath, $"<read error: {ex.Message}>", false));
                continue;
            }

            var remaining = Math.Max(0, MaxAddedFilesTotalChars - usedChars);
            if (remaining == 0)
                break;

            var limit = Math.Min(MaxAddedFileChars, remaining);
            var truncated = content.Length > limit;
            if (truncated)
                content = content[..limit] + "\n<truncated>";

            usedChars += content.Length;
            entries.Add((displayPath, content, truncated));
        }

        if (entries.Count == 0)
            return;

        sb.AppendLine();
        sb.AppendLine("<added_files>");
        sb.AppendLine("Files added via /add are read-only context. Do not edit them unless the user explicitly asks.");
        foreach (var (path, content, _) in entries)
        {
            sb.AppendLine($"<file path=\"{EscapeXml(path)}\">");
            sb.AppendLine("<![CDATA[");
            sb.AppendLine(EscapeCData(content.TrimEnd()));
            sb.AppendLine("]]>");
            sb.AppendLine("</file>");
        }
        sb.AppendLine("</added_files>");
    }

    private static string ResolvePath(string cwd, string path) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(cwd, path));

    private static string MakeRelative(string path, string cwd)
    {
        try
        {
            var abs = Path.GetFullPath(path);
            var cwdFull = Path.GetFullPath(cwd);
            if (abs.StartsWith(cwdFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return abs[(cwdFull.Length + 1)..];
            if (abs.Equals(cwdFull, StringComparison.OrdinalIgnoreCase))
                return ".";
        }
        catch
        {
        }
        return path;
    }

    private static bool IsBinaryFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[Math.Min(8192, (int)Math.Min(stream.Length, 8192))];
            var read = stream.Read(buffer, 0, buffer.Length);
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] == 0)
                    return true;
            }
        }
        catch
        {
        }
        return false;
    }

    private static string EscapeXml(string value) =>
        value
            .Replace("&", "&amp;")
            .Replace("\"", "&quot;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");

    private static string EscapeCData(string value) =>
        value.Replace("]]>", "]]]]><![CDATA[>");
}
