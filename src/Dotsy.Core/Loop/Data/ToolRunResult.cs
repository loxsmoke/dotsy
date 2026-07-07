using Dotsy.Core.Tools;

namespace Dotsy.Core.Loop;

public sealed partial class AgentLoop
{
    private sealed record ToolRunResult(ToolResult Result, long DurationMs, long ApprovalWaitMs);
}
