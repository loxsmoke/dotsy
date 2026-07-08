## 15. System Prompt

### 15.1 Structure and Block Order

The system prompt is assembled fresh before every LLM call by the static
`SystemPromptBuilder.Build(...)`. Blocks are appended in this order:

1. **Base prompt** — identity, conciseness rules, tool-selection policy, grounding/tool-use rules (`SystemPromptBuilder.DefaultBase`, overridable via the `basePrompt` argument)
2. **Environment preamble** — `<env>` block (see §20)
3. **Project context** — `<project_context>` block holding the repo-root `AGENTS.md`, auto-loaded every turn (see §9.3)
4. **Task-file guidance** — `<task_file>` block, only when a `todo.md` exists in the repo root; steers the model to the `Todo` tool
5. **Available skills** — `<available_skills>` XML block listing names and descriptions of model-invocable skills (skills with `disable-model-invocation` are excluded; see §10)
6. **Loaded skills** — `<loaded_skills>` block holding the bodies of skills already loaded into the context (e.g. via `/skill`)
7. **Added files** — `<added_files>` block: files added via `/add` with a "read-only, do not edit" header (CDATA-wrapped)
8. **Repo map** — `<repo_map>` block (if `retrieval.repo_map_tokens > 0`, see §9.2)
9. **Plan mode** — `<plan_mode>` block, injected when `ctx.IsPlanMode` is set
10. **Prior context** — `<prior_context>` block holding the compaction summary plus a continuation instruction, when a summary exists

Tool definitions are **not** embedded in the system string. They are sent as the separate `tools` array in the `ChatRequest` (§6.2), keeping the cacheable system prefix stable.

### 15.2 Static vs Dynamic Content

The **static prefix** — `DefaultBase` identity, principles, tool policy — is identical across all sessions using the same binary version, so Anthropic's prompt cache can serve a cache hit for every turn. The **dynamic suffix** — environment preamble, project/task files, skills, added files, repo map, plan mode, prior context — is rebuilt each turn. `SystemPromptBuilder` is a **static** helper: `Build(...)` re-concatenates the whole prompt on every call (the static prefix is a compile-time `const` string, not cached in a per-session instance).

```csharp
public static class SystemPromptBuilder
{
    public const string ProjectContextFile = "AGENTS.md";
    public const string DefaultBase = "...identity, principles, tool policy...";

    public static string Build(
        DotsyConfig config,
        string cwd,
        LoopContext ctx,
        string? basePrompt = null,          // defaults to DefaultBase
        GitContext? git = null,
        SkillDiscovery? skillDiscovery = null,
        string? repoMap = null);
}
```

### 15.3 Environment Preamble

See §20 for what is injected. The block is wrapped in `<env>` tags and placed immediately after the static identity block, where it becomes part of the stable cacheable prefix as long as the environment does not change (cwd, git branch, date rounded to the day).

### 15.4 Tool Definitions Block

Tool definitions are sent as a structured `tools` array in `ChatRequest`, not embedded in the system string. This matches the Anthropic and OpenAI API contract and keeps the system prompt string shorter. Skill tool, done tool, and MCP tools are added to the array at session start.

### 15.5 Skills and Context File Injection

Skills are lazy: only names and descriptions appear in the `<available_skills>` block in the system prompt. Full skill content is injected via the `skill` tool response at call time (§10). The repo-root `AGENTS.md` is injected in full (capped at 20,000 chars) as a `<project_context>` block — short by convention and stable across turns, so it benefits from being in the cacheable region. See §9.3 for load behaviour and current limitations.

---

