namespace Dotsy.Core.Loop.Data;

public record TokenBudget(
    int ContextWindow,
    int ReserveTokens,
    int KeepRecentTokens,
    int UsedTokens)
{
    public static readonly TokenBudget Empty = new(200_000, 16_384, 20_000, 0);

    public int Usable => ContextWindow - ReserveTokens;
    public float UsagePct => ContextWindow > 0 ? UsedTokens / (float)ContextWindow : 0f;
    public bool ShouldCompact => UsedTokens > Usable;
    public bool ShouldWarn => UsagePct >= 0.60f;

    public TokenBudget WithUsed(int used) => this with { UsedTokens = used };
}
