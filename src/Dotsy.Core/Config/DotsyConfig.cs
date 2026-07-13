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

public interface IModelBaseConfig
{
    string Id { get; set; }
    string ApiKey { get; }
}

public sealed class ModelConfig
{                                         
    public string Provider { get; set; } = ProviderConfig.Anthropic;
    public int MaxOutputTokensPerRequest { get; set; } = 8192;
    public AnthropicConfig Anthropic { get; set; } = new();
    public OpenAiConfig OpenAi { get; set; } = new();
    public AzureConfig Azure { get; set; } = new();
    public OllamaConfig Ollama { get; set; } = new();
    public CompatibleConfig Compatible { get; set; } = new();
    public GeminiConfig Gemini { get; set; } = new();


    public IModelBaseConfig ActiveModel
    { 
        get => Provider.ToLowerInvariant() switch
        {
            ProviderConfig.Anthropic    => Anthropic,
            ProviderConfig.OpenAi       => OpenAi,
            ProviderConfig.Azure        => Azure,
            ProviderConfig.AzureOpenAi  => Azure,
            ProviderConfig.Ollama       => Ollama,
            ProviderConfig.Compatible   => Compatible,
            ProviderConfig.Gemini       => Gemini,
            _ => throw new InvalidOperationException($"Unknown provider: {Provider}")
        };
    }
}

public sealed class AnthropicConfig : IModelBaseConfig
{
    public string Id { get; set; } = "";
    public string ApiKey { get; set; } = "";
}

public sealed class OpenAiConfig : IModelBaseConfig
{
    public string Id { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
}

public sealed class AzureConfig : IModelBaseConfig
{
    public string Id { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public string Deployment { get; set; } = "";
    public string ApiVersion { get; set; } = "2025-01-01";
}

public sealed class OllamaConfig : IModelBaseConfig
{
    public string Id { get; set; } = "";
    
    public string ApiKey => "";

    public string BaseUrl { get; set; } = "http://localhost:11434";
    // Context window (num_ctx) requested when invoking Ollama. Ollama otherwise loads models at a
    // small default (e.g. 8192); this sizes the window the model actually runs with. 128K decimal.
    public int MaxContextTokens { get; set; } = 131_072;
}

public sealed class CompatibleConfig : IModelBaseConfig
{
    public string Id { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "";
}

public sealed class GeminiConfig : IModelBaseConfig
{
    public string Id { get; set; } = "";
    public string ApiKey { get; set; } = "";
}

public sealed class AgentConfig
{
    public int MaxSteps { get; set; } = 0;
    public int MaxTurns { get; set; } = 1000;
    public bool ParallelTools { get; set; } = true;
    public bool AutoCommit { get; set; } = false;
    // Maximum consecutive non-terminal text-only responses. Normal EndTurn and StopSequence
    // responses complete immediately without consuming this guard.
    public int NudgeLimit { get; set; } = 3;
    // Nudge limit used by the interactive TUI, which yields to the user after this many
    // non-advancing responses. Kept separate from NudgeLimit (headless) so raising interactivity
    // headroom doesn't change batch behaviour. 1 = end the turn after a single stalled response.
    public int InteractiveNudgeLimit { get; set; } = 3;
    // When the nudge limit is hit on a recoverable stop (no clean completion / truncated output),
    // inject a recovery hint and retry instead of ending. Bounded by AutoContinueMaxAttempts and
    // progress-guarded: any successful tool call resets the attempt counter, so only a genuinely
    // stalled agent (no progress) exhausts the attempts and stops.
    public bool AutoContinueOnNudge { get; set; } = true;
    public int AutoContinueMaxAttempts { get; set; } = 3;
    // A text-only response that cleanly ends the turn (StopReason.EndTurn) normally completes the
    // run. But weaker models often *announce* the next step ("Let me implement this now.") and end
    // the turn without calling a tool — no work gets done. When enabled, such a response is treated
    // as a recoverable stall: a hint is injected and the model is retried instead of stopping.
    // Shares the AutoContinueMaxAttempts budget and is progress-guarded (any tool call resets it),
    // so a genuine final answer (no announced next action) still ends immediately.
    public bool AutoContinueOnEndTurnIntent { get; set; } = true;
    // Runtime flag (not a user setting): true when running non-interactively (headless `run`), where
    // there is no user to answer a clarifying question. Set by the CLI. When set, a text-only turn
    // that ends by asking the user to clarify is treated as a recoverable stall rather than an end.
    public bool Headless { get; set; }
    // Rolling-window loop guard: if a tool-call signature recurs RepeatThreshold times within the
    // last RepeatWindowTurns turns, the agent is nudged out of a multi-turn read/search cycle.
    // Set RepeatThreshold to 0 to disable.
    public int RepeatWindowTurns { get; set; } = 8;
    public int RepeatThreshold { get; set; } = 3;
    public bool AutoLint { get; set; } = false;
    public bool AutoTest { get; set; } = false;
    // Completion guard: if the most recent build/test command in the session failed (non-zero
    // exit), refuse a completion signal (e.g. the Done tool) and inject a corrective hint so the
    // model fixes the failure instead of declaring success over a red build. Weaker models often
    // narrate "build succeeded" while the last build actually exited non-zero. Bounded by
    // AutoContinueMaxAttempts and progress-guarded (a subsequent passing build clears the flag).
    // Set false to always trust the completion signal.
    public bool VerifyBuildBeforeComplete { get; set; } = true;
    // Read de-duplication: when the model re-reads a file it already read, whose content is STILL
    // present verbatim in the live context (not yet summarized/compacted away) and which has not
    // changed on disk, return a short "already read" stub instead of re-injecting the whole file.
    // This is compaction-safe: if the earlier read has been summarized out of context, the full
    // file is returned again so the model never loses content it needs. Set false to always
    // re-read. See ReadDedup.
    public bool DedupeReads { get; set; } = true;
    // Read-before-edit guard: reject Edit/MultiEdit of a file the model has not Read this session,
    // or whose on-disk state changed since the last read. Line-range edits additionally require a
    // fresh Read after the model's own last edit/write of the file, because line numbers go stale
    // the moment the file changes — observed dogfooding failure: the model line-edits from stale
    // numbers and silently corrupts brace structure, spiralling into rebuild/rewrite loops. The
    // rejection tells the model to Read the file, so it self-corrects in one round trip. See
    // ReadBeforeEdit. Set false to allow blind edits.
    public bool RequireReadBeforeEdit { get; set; } = true;
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
    public bool ToolPairSummarize { get; set; } = true;
    // When summarizing old tool pairs, keep the MOST RECENT read of each distinct file verbatim
    // instead of collapsing it. Weaker models otherwise re-read the same file repeatedly once its
    // content is summarized away; preserving the latest read keeps it available (and lets read
    // de-dup catch any repeat). Superseded/older reads of the same file are still summarized.
    public bool PreserveLatestReads { get; set; } = true;
}

public sealed class RetrievalConfig
{
    // Token budget for the repository map injected at session start. A richer map up front lets
    // the model orient without a burst of exploratory whole-file reads.
    public int RepoMapTokens { get; set; } = 4096;
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
