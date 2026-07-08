## 9. Code Retrieval

Strategy: **tool-mediated by default** (simpler, no pre-indexing required). An optional proactive **repo map** is built with Roslyn and injected as context, similar to **aider**'s tree-sitter + PageRank approach.

### 9.1 Tool-Mediated Retrieval

The LLM uses `Grep`, `Glob`, `FindDefs`, and `Read` to locate relevant code. This is the same approach as **opencode**, **cline**, and **pi**. No background indexing; the model decides what to look up.

**`Grep` limits:** 100 matches, 50 KB, 2 000 lines (whichever is first). Long lines are truncated at 500 characters.

**`FindDefs`** uses Roslyn's `CSharpSyntaxTree` to extract top-level type names and member signatures from a directory (max 50 files). Returns a compact outline (file path + declarations) without full method bodies. Equivalent to **cline**'s `list_code_definition_names` for C#.

### 9.2 Proactive Repo Map (Optional)

Enabled when `retrieval.repo_map_tokens > 0`.

**Indexing** (lazy, per-build):
1. `RoslynIndex` parses each `.cs` file under the cwd and extracts an outline (types, member signatures) plus referenced type names.
2. Outlines are cached in a JSON file at `.dotsy/cache/roslyn-index-v2.json`, keyed by file path with the last-write timestamp — unchanged files are not re-parsed. (No SQLite; the cache is a plain serialized dictionary.)

**Graph**:
- Nodes = source files
- Directed edges = "file A references symbol defined in file B", weighted by reference frequency.

**Ranking**:
- PageRank on the graph (use a simple C# implementation — MathNet.Numerics or inline power iteration).
- Personalization vector boosts files already mentioned in the conversation or appearing in the user's current message.
- High-frequency noise names (generated code, auto-properties) are down-weighted.

**Injection**:
- Top-ranked files are rendered as a compact outline: `// Namespace.ClassName\n  Method1Signature\n  Method2Signature\n...`
- Map grows until `retrieval.repo_map_tokens` budget is consumed.
- Inserted into the system prompt as a `<repo_map>` block, updated on every turn.
- When no files are explicitly added to context, map budget scales up 8× (same as **aider**'s `map_mul_no_files`).

### 9.3 Project Context Files

`SystemPromptBuilder` loads `AGENTS.md` from the repo root (the session cwd) and injects its contents into the system prompt as a `<project_context>` block, introduced by a line marking the content as authoritative project instructions ("prefer them over assumptions"). This follows the cross-tool `AGENTS.md` convention, so the same file works with other agents.

Behaviour:
- Injected on **every turn** (rebuilt by `Build()`), placed immediately after the environment block and before the `<available_skills>` block. Because the file rarely changes, the block stays stable across turns and remains part of the cacheable prefix in practice.
- Content is capped at 20,000 chars; longer files are truncated with a trailing `<truncated>` marker.
- A missing, empty, or unreadable file is skipped silently — it never fails a turn.
- The filename is exposed as `SystemPromptBuilder.ProjectContextFile`.

**Not yet implemented:** `CLAUDE.md` / `.dotsy/context.md` fallbacks, a cwd→repo-root walk-up, and a user-level (global) context file. Currently only `AGENTS.md` at the cwd is read. Loading is also **not** gated by `--bare` yet (the flag is parsed but not wired through), so the §18 claim that `--bare` skips `AGENTS.md` is aspirational.

---

