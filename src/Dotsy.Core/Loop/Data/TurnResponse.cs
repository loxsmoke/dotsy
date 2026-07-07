using System.Text;
using Dotsy.Core.Loop.Data;
using Dotsy.Core.Providers;

namespace Dotsy.Core.Loop;

public sealed partial class AgentLoop
{
    private sealed class TurnResponse(int nextToolEventIndex, TokenBudget budget)
    {
        public StringBuilder Text { get; } = new();
        public StringBuilder Thinking { get; } = new();
        public List<(string Id, string Name, string Args)> ToolCalls { get; } = [];
        public Dictionary<string, int> PendingToolIndex { get; } = [];
        public int NextToolEventIndex { get; set; } = nextToolEventIndex;
        public TokenBudget Budget { get; set; } = budget;
        public bool HadError { get; set; }
        public Exception? ContextLengthError { get; set; }
        public StopReason? StopReason { get; set; }
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
        public int CacheReadTokens { get; set; }
        public int CacheWriteTokens { get; set; }
    }
}
