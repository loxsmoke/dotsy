using Dotsy.Core.Config;
using Dotsy.Core.Loop;
using Dotsy.Core.Loop.Data;
using Dotsy.Core.Providers;

namespace Dotsy.Core.Tests;

[TestClass]
public sealed class RequestBuilderTests
{
    private static DotsyConfig Config(int contextWindow = 200_000, int reserve = 16_384) =>
        new()
        {
            Model    = new ModelConfig { Anthropic = new() { Id = "test" }, MaxOutputTokensPerRequest = 1024 },
            Agent    = new AgentConfig(),
            Compaction  = new CompactionConfig { ReserveTokens = reserve },
            Retrieval   = new RetrievalConfig(),
            Skills      = new SkillsConfig { Paths = [] },
            Git         = new GitConfig(),
            Tui         = new TuiConfig(),
            Permissions = new PermissionsConfig { AlwaysAllow = [], NeverAllow = [] },
            Session     = new SessionConfig()
        };

    // ── ContextTooSmall ───────────────────────────────────────────────────────

    [TestMethod]
    [ExpectedException(typeof(ContextTooSmallException))]
    public void NonNegotiableBlock_TooLarge_ThrowsContextTooSmallException()
    {
        // usable = 1000 - 100 = 900; system prompt alone is 4000 chars ≈ 1000 tokens > 900
        var config = Config(contextWindow: 1_000, reserve: 100);
        var ctx    = new LoopContext
        {
            TokenBudget = new TokenBudget(1_000, 100, 500, 0)
        };

        var systemPrompt = new string('x', 4_000); // ~1000 tokens (4 chars/token)

        RequestBuilder.Build(config, systemPrompt, ctx, []);
    }

    // ── Pruning oldest messages ───────────────────────────────────────────────

    [TestMethod]
    public void PrunesOldestMessages_ToFitBudget()
    {
        // usable = 10000 - 1000 = 9000 tokens
        // each message ~ 2000 chars = 500 tokens; 3 messages = 1500 tokens > would fit,
        // but we use a tiny budget so only the last 2 can fit
        const int usable = 1_200; // tokens
        var config = Config(contextWindow: usable + 1_000, reserve: 1_000);
        var ctx    = new LoopContext
        {
            TokenBudget = new TokenBudget(usable + 1_000, 1_000, 500, 0)
        };

        // Each message is 2000 chars ≈ 500 tokens
        var msg = new string('a', 2_000);
        ctx.Messages.Add(new UserMessage([new TextBlock(msg)]));      // oldest — should be pruned
        ctx.Messages.Add(new AssistantMessage([new TextBlock(msg)])); // kept
        ctx.Messages.Add(new UserMessage([new TextBlock(msg)]));      // kept (last 2)

        var req = RequestBuilder.Build(config, "sys", ctx, []);

        // With a 1200-token budget for messages and 3×500-token msgs, only 2 fit
        // "sys" ≈ 1 token (non-negotiable); remaining = 1199 tokens → fits 2 messages (1000 tokens)
        Assert.IsTrue(req.Messages.Count <= 2, $"Expected ≤2 messages after pruning, got {req.Messages.Count}");
    }
}
