## 4. Project Structure

```
Dotsy/
├── Dotsy.slnx
├── src/
│   ├── Dotsy.Cli/               # Entry point, CLI parsing, TUI bootstrap
│   │   ├── Program.cs           # RootCommand + `run`/`skills` subcommands, headless + TUI hosts
│   │   ├── HeadlessStreamJson.cs
│   │   ├── SlashCommands/       # ISlashCommand registry + /help, /config, /model, /resume, …
│   │   └── Tui/                 # Terminal.Gui layout, panels, renderers, approval overlay
│   ├── Dotsy.Core/              # Provider-agnostic agent logic
│   │   ├── Loop/                # AgentLoop, RequestBuilder, compaction (ToolPairSummarizer),
│   │   │                        #   RetryPolicy, PermissionStore, SystemPromptBuilder, Data/
│   │   ├── Tools/               # ITool, ToolRegistry, built-in tools, RipgrepBinary
│   │   ├── Skills/              # SkillDiscovery, SkillLoader, ParsedSkill
│   │   ├── Retrieval/           # RepoMap (PageRank), RoslynIndex
│   │   ├── Session/             # SessionStore, SessionLoader, TrajectoryRecorder
│   │   ├── Config/              # ConfigLoader, ConfigEditor, DotsyConfig, ProviderConfig
│   │   ├── Providers/           # IProvider, ChatRequest, ProviderEvents (abstractions only)
│   │   └── Git/                 # GitContext, GitIntegration
│   ├── Dotsy.Providers/         # AI provider implementations
│   │   ├── Anthropic/
│   │   ├── OpenAi/
│   │   ├── AzureOpenAi/
│   │   ├── Gemini/
│   │   ├── Ollama/
│   │   ├── OpenAiCompatible/    # Generic fallback (OpenRouter, Together, DeepSeek, …)
│   │   ├── ModelCatalog.cs
│   │   ├── ProviderRegistry.cs
│   │   └── RetryingProvider.cs
│   └── Dotsy.Mcp/               # MCP client, server discovery (McpClient, McpManager, McpTool)
└── tests/
    ├── Dotsy.Core.Tests/
    ├── Dotsy.Cli.Tests/
    ├── Dotsy.Providers.Tests/
    └── Dotsy.Mcp.Tests/
```

The provider *abstractions* (`IProvider`, `ChatRequest`, `ProviderEvent`, `ModelInfo`) live in
`Dotsy.Core/Providers/`; the concrete provider implementations live in the `Dotsy.Providers` project.
Compaction is not a separate folder — it lives in `Dotsy.Core/Loop/` (`AgentLoop` compaction steps,
`ToolPairSummarizer`, `RequestBuilder`) with token accounting under `Loop/Data/` (`TokenBudget`,
`TokenUsageTracker`).

---

