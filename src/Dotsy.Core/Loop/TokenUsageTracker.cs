using Dotsy.Core.Providers;

namespace Dotsy.Core.Loop;

public sealed class TokenUsageTracker
{
    public int TotalInputTokens { get; private set; }
    public int TotalOutputTokens { get; private set; }

    public void RecordUsage(UsageUpdate usage)
    {
        TotalInputTokens += usage.InputTokens;
        TotalOutputTokens += usage.OutputTokens;
    }
}
