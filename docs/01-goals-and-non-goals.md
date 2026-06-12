## 1. Goals and Non-Goals

### Goals

- A single self-contained binary (`dotsy.exe`) that runs in any terminal.
- Advanced TUI: panels, real-time streaming output, tool-call display, context-usage meter — not plain `Console.WriteLine`.
- Multi-provider AI support: Anthropic, OpenAI, Azure OpenAI, Ollama (local), OpenRouter, and any OpenAI-compatible endpoint.
- Full agentic loop: tool use, reflection, parallel tool execution, subagent delegation.
- Skills system compatible with the cross-tool `SKILL.md` convention (shared with **opencode**, **cline**, **pi**, **goose**, **continue**).
- Proactive context-window management: token tracking, automatic summarisation, hard-error recovery.
- First-class C# / .NET code intelligence via Roslyn (replaces tree-sitter used by other agents).
- Git-aware: reads repo structure as context; optionally auto-commits edits.
- MCP (Model Context Protocol) client: connect to external MCP servers for additional tools.

### Non-Goals

- GUI desktop application (Electron / WPF / Avalonia) — deferred.
- IDE plugin (VS Code / Rider) — deferred.
- Self-hosted inference server (no bundled LLM weights).
- Web-based multi-user server.

---

