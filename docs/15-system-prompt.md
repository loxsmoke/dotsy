## 15. System Prompt

### 15.1 Structure and Block Order

The system prompt is assembled fresh before every LLM call by `SystemPromptBuilder`. Blocks are concatenated in this fixed order:

1. **Identity and principles** — who the agent is; conciseness rules; action-care policy (reversibility, blast radius)
2. **Tool-use policy** — prefer native tools over shell for observability; parallel tool execution guidance; file-search preferences (part of the base prompt)
3. **Environment preamble** — see §20
4. **Project context** — `<project_context>` block holding the repo-root `AGENTS.md`, auto-loaded every turn (see §9.3)
5. **Available skills** — `<available_skills>` XML block listing names and descriptions (see §10)
6. **Repo map** — `<repo_map>` block (if enabled, see §9.2)
7. **Read-only files** — files added via `/add` with a "do not edit these" header
8. **Mode fragment** — injected when plan mode is active

Tool definitions are **not** embedded in the system string. They are sent as the separate `tools` array in the `ChatRequest` (§6.2), keeping the cacheable system prefix stable.

### 15.2 Static vs Dynamic Content

The **static prefix** — identity, principles, tool policy — is identical across all sessions using the same binary version. It is placed first so Anthropic's prompt cache can serve a cache hit for every turn. The **dynamic suffix** — environment preamble, skills, project files, repo map, mode fragment — is rebuilt each turn. A `SystemPromptBuilder` object is created per session; the static portion is computed once and stored; dynamic blocks are appended on each `Build()` call.

```csharp
public class SystemPromptBuilder
{
    private readonly string _staticPrefix;   // computed once in ctor

    public string Build(SessionContext ctx)
    {
        var sb = new StringBuilder(_staticPrefix);
        sb.Append(BuildEnvironmentBlock(ctx));
        sb.Append(BuildProjectContextBlock(ctx));   // repo-root AGENTS.md
        sb.Append(BuildSkillsBlock(ctx));
        sb.Append(BuildRepoMapBlock(ctx));
        if (ctx.IsPlanMode) sb.Append(PlanModeFragment);
        return sb.ToString();
    }
}
```

### 15.3 Environment Preamble

See §20 for what is injected. The block is wrapped in `<env>` tags and placed immediately after the static identity block, where it becomes part of the stable cacheable prefix as long as the environment does not change (cwd, git branch, date rounded to the day).

### 15.4 Tool Definitions Block

Tool definitions are sent as a structured `tools` array in `ChatRequest`, not embedded in the system string. This matches the Anthropic and OpenAI API contract and keeps the system prompt string shorter. Skill tool, done tool, and MCP tools are added to the array at session start.

### 15.5 Skills and Context File Injection

Skills are lazy: only names and descriptions appear in the `<available_skills>` block in the system prompt. Full skill content is injected via the `skill` tool response at call time (§10). The repo-root `AGENTS.md` is injected in full (capped at 20,000 chars) as a `<project_context>` block — short by convention and stable across turns, so it benefits from being in the cacheable region. See §9.3 for load behaviour and current limitations.

---

