namespace Dotsy.Core.Session.Data;

public sealed class SessionUsage
{
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int CacheReadTokens { get; set; }
    public int CacheWriteTokens { get; set; }
    public int ContextWindowTokens { get; set; }
    public int MaxOutputTokens { get; set; }
    public int ReserveTokens { get; set; }
    public int UsedTokens { get; set; }
}
