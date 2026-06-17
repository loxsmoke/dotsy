using Dotsy.Core.Config;
using Dotsy.Core.Loop;
using Dotsy.Core.Providers;
using Dotsy.Core.Session;
using Dotsy.Core.Tools;
using Dotsy.Mcp;

namespace Dotsy.Cli.Tui;

/// <summary>
/// Static context shared between Program.cs and AgentWindow since
/// Terminal.Gui's Application.Run&lt;T&gt;() uses the default constructor.
/// </summary>
public static class TuiSessionContext
{
    public static DotsyConfig Config { get; set; } = DefaultConfig.Create();
    public static string Cwd { get; set; } = Environment.CurrentDirectory;
    public static string? ProjectConfigPath { get; set; }
    public static AgentLoop? Loop { get; set; }
    public static LoopContext? LoopCtx { get; set; }
    public static PermissionStore? Permissions { get; set; }
    public static ToolRegistry? Registry { get; set; }
    public static SessionStore? Session { get; set; }
    public static TrajectoryRecorder? Trajectory { get; set; }
    public static McpManager? McpManager { get; set; }
    public static List<string> StartupMessages { get; } = [];
    public static Action<string>? StatusUpdate { get; set; }

    /// <summary>
    /// Recreates the provider and loop from the current Config state.
    /// Set by Program.cs; throws on invalid provider config.
    /// </summary>
    public static Func<AgentLoop>? LoopFactory { get; set; }

    /// <summary>
    /// Looks up <see cref="ModelInfo"/> (context window + source) for a model id using the current
    /// provider/config. Set by Program.cs; used by /model to report the live context window.
    /// </summary>
    public static Func<string, Task<ModelInfo>>? ModelInfoLookup { get; set; }
}
