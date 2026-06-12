namespace Dotsy.Core.Config;

public sealed class DotsyConfig
{
    public ModelConfig Model { get; set; } = new();
    public AgentConfig Agent { get; set; } = new();
    public CompactionConfig Compaction { get; set; } = new();
    public RetrievalConfig Retrieval { get; set; } = new();
    public SkillsConfig Skills { get; set; } = new();
    public McpConfig Mcp { get; set; } = new();
    public GitConfig Git { get; set; } = new();
    public TuiConfig Tui { get; set; } = new();
    public PermissionsConfig Permissions { get; set; } = new();
    public SessionConfig Session { get; set; } = new();
    public TrajectoryConfig Trajectory { get; set; } = new();
}

public sealed class ModelConfig
{
    public string Provider { get; set; } = "anthropic";
    public string Id { get; set; } = "claude-opus-4-7";
    public int MaxOutputTokensPerRequest { get; set; } = 8192;
    public AnthropicConfig Anthropic { get; set; } = new();
    public OpenAiConfig OpenAi { get; set; } = new();
    public AzureConfig Azure { get; set; } = new();
    public OllamaConfig Ollama { get; set; } = new();
    public CompatibleConfig Compatible { get; set; } = new();
}

public sealed class AnthropicConfig
{
    public string ApiKey { get; set; } = "";
}

public sealed class OpenAiConfig
{
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
}

public sealed class AzureConfig
{
    public string ApiKey { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public string Deployment { get; set; } = "";
    public string ApiVersion { get; set; } = "2025-01-01";
}

public sealed class OllamaConfig
{
    public string BaseUrl { get; set; } = "http://localhost:11434";
}

public sealed class CompatibleConfig
{
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "";
}

public sealed class AgentConfig
{
    public int MaxSteps { get; set; } = 0;
    public int MaxTurns { get; set; } = 1000;
    public bool ParallelTools { get; set; } = true;
    public bool AutoCommit { get; set; } = false;
    public int NudgeLimit { get; set; } = 3;
    public bool AutoLint { get; set; } = false;
    public bool AutoTest { get; set; } = false;
    public int MaxReflections { get; set; } = 3;
    public bool InjectEnvironment { get; set; } = true;
    public bool InjectGitStatus { get; set; } = true;
}

public sealed class CompactionConfig
{
    public bool Enabled { get; set; } = true;
    public float ThresholdPct { get; set; } = 0.80f;
    public int ReserveTokens { get; set; } = 16_384;
    public int KeepRecentTokens { get; set; } = 20_000;
    public bool ToolPairSummarise { get; set; } = true;
}

public sealed class RetrievalConfig
{
    public int RepoMapTokens { get; set; } = 1024;
    public int RipgrepMaxMatches { get; set; } = 100;
    public int RipgrepMaxBytes { get; set; } = 51_200;
}

public sealed class SkillsConfig
{
    public List<string> Paths { get; set; } = [];
    public bool CrossTool { get; set; } = true;
}

public sealed class McpConfig
{
    public List<McpServerConfig> Servers { get; set; } = [];
}

public sealed class McpServerConfig
{
    public string Name { get; set; } = "";
    public McpTransport Transport { get; set; } = McpTransport.Stdio;
    public string Command { get; set; } = "";
    public string[] Args { get; set; } = [];
    public string Url { get; set; } = "";
}

public enum McpTransport
{
    Stdio,
    Http
}

public sealed class GitConfig
{
    public bool AutoStage { get; set; } = false;
    public int DiffContextLines { get; set; } = 3;
}

public sealed class TuiConfig
{
    public int LeftPanelWidthPercentage { get; set; } = 70;
    public string Theme { get; set; } = "dark";

    private volatile bool _verbose;
    public bool Verbose
    {
        get => _verbose;
        set => _verbose = value;
    }
}

public sealed class PermissionsConfig
{
    public List<string> AlwaysAllow { get; set; } =
    [
        "Shell(git status)", "Shell(git log *)", "Shell(git diff *)",
        "Shell(dotnet build *)", "Shell(dotnet test *)", "Shell(dotnet run *)",
        "Shell(ls *)", "Shell(dir *)"
    ];
    public List<string> NeverAllow { get; set; } = [];
}

public sealed class SessionConfig
{
    public int CleanupDays { get; set; } = 30;
    public bool LogEnabled { get; set; } = true;
    public string LogDir { get; set; } = ".dotsy/sessions";
}

public sealed class TrajectoryConfig
{
    public bool Enabled { get; set; } = false;
    public string Dir { get; set; } = ".dotsy/trajectories";
}
