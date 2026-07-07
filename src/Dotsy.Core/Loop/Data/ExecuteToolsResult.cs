namespace Dotsy.Core.Loop;

public sealed partial class AgentLoop
{
    private sealed class ExecuteToolsResult
    {
        public required ToolRunResult[] Results { get; init; }
        public bool AnyWriteTools { get; set; }
        public bool SignalCompletion { get; set; }
        public List<string> AffectedPaths { get; } = [];
    }
}
