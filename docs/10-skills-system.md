## 10. Skills System

### 10.1 Format

```markdown
---
name: aspnet-conventions
description: ASP.NET Core coding conventions and patterns. Use when writing controllers, middleware, or DI configuration.
allowed-tools: read,edit,write,shell
---

# Skill instructions in Markdown
```

Required frontmatter: `name`, `description`. Optional: `allowed-tools` (comma-separated subset of tool names that this skill permits), `disable-model-invocation` (true = never auto-selected, only via slash command).

### 10.2 Discovery

At session start, `SkillDiscovery` scans in order (first-found wins on name collision; collision warnings emitted to tool log):

1. CLI `--skill <path>` arguments
2. Project: `.dotsy/skills/<name>/`, `.agents/skills/<name>/`
3. Global: `~/.config/dotsy/skills/<name>/`
4. Cross-tool (when `skills.cross_tool = true`): `~/.agents/skills/<name>/`, `~/.claude/skills/<name>/`
5. Config `skills.paths[]` array entries

Each skill directory must contain `SKILL.md`. Additional files in the directory are companion files accessible via the `read` tool.

### 10.3 Selection

Available skill names and descriptions are injected into the system prompt as an `<available_skills>` XML block. The model calls the `skill` tool with the skill name when a task matches. Permission is requested once per skill per session; subsequent calls are auto-allowed.

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

