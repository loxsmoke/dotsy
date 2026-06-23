namespace Dotsy.Core.Config;

public static class DefaultConfig
{
    public static DotsyConfig Create() => new()
    {
        Model = new ModelConfig
        {
            Provider = "anthropic",
            MaxOutputTokensPerRequest = 8192,
            Anthropic = new AnthropicConfig { Id = "", ApiKey = "" },
            OpenAi = new OpenAiConfig { Id = "", ApiKey = "", BaseUrl = "https://api.openai.com/v1" },
            Azure = new AzureConfig { Id = "", ApiKey = "", Endpoint = "", Deployment = "", ApiVersion = "2025-01-01" },
            Ollama = new OllamaConfig { Id = "", BaseUrl = "http://localhost:11434", MaxContextTokens = 131_072 },
            Compatible = new CompatibleConfig { Id = "", ApiKey = "", BaseUrl = "" },
            Gemini = new GeminiConfig { Id = "", ApiKey = "" }
        },
        Agent = new AgentConfig
        {
            MaxSteps = 0,
            MaxTurns = 1000,
            ParallelTools = true,
            AutoCommit = false,
            NudgeLimit = 3,
            RepeatWindowTurns = 8,
            RepeatThreshold = 3,
            AutoLint = false,
            AutoTest = false,
            MaxReflections = 3,
            InjectEnvironment = true,
            InjectGitStatus = true
        },
        Compaction = new CompactionConfig
        {
            Enabled = true,
            ThresholdPct = 0.80f,
            ReserveTokens = 16_384,
            KeepRecentTokens = 20_000,
            ToolPairSummarize = true
        },
        Retrieval = new RetrievalConfig
        {
            RepoMapTokens = 1024,
            RipgrepMaxMatches = 100,
            RipgrepMaxBytes = 51_200
        },
        Skills = new SkillsConfig
        {
            Paths = [],
            CrossTool = true
        },
        Mcp = new McpConfig
        {
            Servers = []
        },
        Git = new GitConfig
        {
            AutoStage = false,
            DiffContextLines = 3
        },
        Tui = new TuiConfig
        {
            LeftPanelWidthPercentage = 70,
            Theme = "dark"
        },
        Permissions = new PermissionsConfig
        {
            AlwaysAllow =
            [
                "Shell(git status)", "Shell(git log *)", "Shell(git diff *)",
                "Shell(dotnet build *)", "Shell(dotnet test *)", "Shell(dotnet run *)",
                "Shell(ls *)", "Shell(dir *)"
            ],
            NeverAllow = []
        },
        Session = new SessionConfig
        {
            CleanupDays = 30
        }
    };
}
