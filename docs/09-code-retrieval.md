## 9. Code Retrieval

Strategy: **tool-mediated by default** (simpler, no pre-indexing required). An optional proactive **repo map** is built with Roslyn and injected as context, similar to **aider**'s tree-sitter + PageRank approach.

### 9.1 Tool-Mediated Retrieval

The LLM uses `grep`, `glob`, `find_definitions`, and `read` to locate relevant code. This is the same approach as **opencode**, **cline**, and **pi**. No background indexing; the model decides what to look up.

**`grep` limits:** 100 matches, 50 KB, 2 000 lines (whichever is first). Long lines are truncated at 500 characters.

**`find_definitions`** uses Roslyn's `CSharpSyntaxTree` to extract top-level type names and member signatures from a directory (max 50 files). Returns a compact outline (file path + declarations) without full method bodies. Equivalent to **cline**'s `list_code_definition_names` for C#.

### 9.2 Proactive Repo Map (Optional)

Enabled when `retrieval.repo_map_tokens > 0`.

**Indexing** (background, lazy):
1. Roslyn parses each tracked `.cs` file and extracts definitions (types, methods) and references (call sites, using directives).
2. Tags are cached in SQLite keyed by `(file_path, last_write_utc)` — unchanged files are not re-parsed.

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

On session start, walk up from cwd to the repo root and load the first found `AGENTS.md`, `CLAUDE.md`, or `.dotsy/context.md`. Inject content into the system prompt before the first LLM call (same as **pi**'s `loadProjectContextFiles`).

---

