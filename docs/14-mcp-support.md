## 14. MCP Support

### 14.1 MCP Client

`Dotsy.Mcp` implements the MCP client protocol (JSON-RPC 2.0 over stdio or HTTP). At session start, configured MCP servers are started (or connected to) and their tool manifests are fetched via `tools/list`.

Each discovered MCP tool is registered in `ToolRegistry` as an `McpTool` wrapper that forwards calls via `tools/call`.

### 14.2 Server Configuration (TOML)

```toml
[[mcp.servers]]
name    = "filesystem"
command = "npx"
args    = ["-y", "@modelcontextprotocol/server-filesystem", "/path"]
env     = {}

[[mcp.servers]]
name    = "github"
url     = "http://localhost:3000/mcp"   # HTTP transport
```

### 14.3 MCP Tool Display

MCP tools appear in the TUI tool log with a `[mcp:<server>]` prefix so the user can distinguish built-in from external tools.

---

## Appendix A — Compaction Comparison (Best Practices Adopted)

| Practice | Source agent | Adopted |
|----------|-------------|---------|
| Proactive threshold before LLM call (not reactive) | **goose**, **pi**, **opencode** | Yes |
| Keep recent N tokens verbatim; summarise head | **pi**, **opencode** | Yes |
| Background tool-pair summarisation | **goose** | Yes |
| Exact API token counts, estimate as fallback | **pi** | Yes |
| Hidden continuation message after compaction | **goose** | Yes |
| Incremental summary update (merge, not replace) | **opencode**, **pi** | Yes |
| Preserve non-negotiable block check | **continue** | Yes |
| Per-provider hard-error pattern matching | **cline** | Yes |

## Appendix B — Retrieval Comparison (Best Practices Adopted)

| Practice | Source agent | Adopted |
|----------|-------------|---------|
| Tool-mediated search (LLM decides) | **opencode**, **cline**, **pi**, **goose** | Yes (default) |
| Proactive repo map with ranking | **aider** | Yes (opt-in) |
| Ripgrep for text search | **opencode**, **cline**, **pi**, **continue** | Yes |
| Language-aware symbol extraction | **cline** (tree-sitter), **continue** (tree-sitter) | Yes (Roslyn for C#) |
| Project context file auto-load on start | **pi** | Yes |
| Result size limits (matches + bytes + lines) | **pi**, **cline** | Yes |


---

---

