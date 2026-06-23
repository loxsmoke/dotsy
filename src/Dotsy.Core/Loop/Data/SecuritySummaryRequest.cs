using Dotsy.Core.Config;
using Dotsy.Core.Tools;

namespace Dotsy.Core.Loop.Data;

public sealed record SecuritySummaryRequest(
    DotsyConfig Config,
    PermissionStore Permissions,
    string Cwd,
    ToolRegistry? Registry = null,
    LoopContext? LoopContext = null,
    bool Headless = false);
