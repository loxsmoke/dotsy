## 10. Skills System

### 10.1 Format

```markdown
---
name: aspnet-conventions
description: ASP.NET Core coding conventions and patterns. Use when writing controllers, middleware, or DI configuration.
allowed-tools: Read,Edit,Write,Shell
---

# Skill instructions in Markdown
```

Required frontmatter: `name`, `description`. Optional: `allowed-tools` (comma-separated subset of tool names that this skill permits), `disable-model-invocation` (true = never auto-selected, only via slash command).

### 10.2 Discovery

At session start, `SkillDiscovery` scans in this order (first-found wins on name collision; later duplicates are suppressed):

1. Config `skills.paths[]` array entries (absolute, or relative to cwd)
2. Project: `.dotsy/skills/` and `.agents/skills/` in **every ancestor directory**, walking up from the cwd to the filesystem root
3. Global: `~/.config/dotsy/skills/`
4. Cross-tool (when `skills.cross_tool = true`): `~/.agents/skills/`, `~/.claude/skills/`

There is no `--skill` CLI flag; extra directories are added through `skills.paths`.

Within each search directory a skill may be either a flat Markdown file (`{name}.md`) or a
sub-directory with an entry-point `SKILL.md` (`{name}/SKILL.md`). For a `SKILL.md` skill the skill
name is the directory name; for a flat file it is the filename stem. Other files in a `SKILL.md`
skill's directory are companion files accessible via the `Read` tool.

### 10.3 Selection

Available skill names and descriptions are injected into the system prompt as an `<available_skills>` XML block. The model calls the `Skill` tool with the skill name when a task matches. Permission is requested once per skill per session; subsequent calls are auto-allowed.

Slash command `/skill <name>` in the input bar bypasses the LLM and loads the skill immediately.

### 10.4 Injection

The `skill` tool returns:
```xml
<skill_content name="aspnet-conventions">
  [full Markdown body, frontmatter stripped]

  <companion_files>
    <file>file:///path/to/skill-dir/example.cs</file>
  </companion_files>
</skill_content>
```

---

