## 4. Project Structure

```
Dotsy/
├── Dotsy.sln
├── src/
│   ├── Dotsy.Cli/               # Entry point, CLI parsing, TUI bootstrap
│   │   ├── Program.cs
│   │   ├── Commands/            # run, config, skills, sessions subcommands
│   │   └── Tui/                 # Terminal.Gui layout, panels, input handler
│   ├── Dotsy.Core/              # Provider-agnostic agent logic
│   │   ├── Loop/                # AgentLoop, LoopContext, LoopEvent
│   │   ├── Tools/               # ITool, ToolRegistry, built-in tools
│   │   ├── Skills/              # SkillDiscovery, SkillLoader
│   │   ├── Compaction/          # TokenCounter, CompactionPolicy, Summariser
│   │   ├── Retrieval/           # RepoMap, RipgrepSearch, RoslynIndex
│   │   ├── Session/             # SessionStore, MessageHistory
│   │   └── Git/                 # GitContext, AutoCommit
│   ├── Dotsy.Providers/         # AI provider implementations
│   │   ├── Anthropic/
│   │   ├── OpenAi/
│   │   ├── AzureOpenAi/
│   │   ├── Ollama/
│   │   └── OpenAiCompatible/    # Generic fallback
│   └── Dotsy.Mcp/               # MCP client, server discovery
└── tests/
    ├── Dotsy.Core.Tests/
    └── Dotsy.Providers.Tests/
```

---

