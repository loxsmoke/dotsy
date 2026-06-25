## 11. Context Window Management

### 11.1 Token Tracking

```csharp
public record TokenBudget(
    int ContextWindow,      // from ModelInfo
    int ReserveTokens,      // from config (default 16384)
    int KeepRecentTokens,   // from config (default 20000)
    int UsedTokens          // from last API UsageUpdate
)
{
    int Usable => ContextWindow - ReserveTokens;
    float UsagePct => UsedTokens / (float)ContextWindow;
    bool ShouldCompact => UsedTokens > Usable;
    bool ShouldWarn => UsagePct >= 0.60f;
}
```

Token counts come from the provider's `UsageUpdate` event. The fallback estimator (`text.Length / 4`) is used only when the provider does not report usage.

### 11.2 Proactive Compaction

**Threshold:** `UsedTokens > ContextWindow - ReserveTokens` (absolute, not percentage). Checked between turns via the `MaybeCompact` step at the top of the loop — never mid-stream.

**Algorithm** (**opencode** + **pi** hybrid):
1. Walk messages backwards from most recent, accumulate token estimates.
2. Everything within the last `keep_recent_tokens` (default 20 000) is kept verbatim ("tail").
3. Everything before the cut point is the "head" — submitted to the LLM for summarisation.
4. If the most recent single turn itself exceeds the budget, split it: keep the suffix verbatim and summarise the prefix.
5. Tool-call outputs (not inputs) in the head are pruned first before full summarisation, to reclaim tokens cheaply.

**Background tool-pair summarisation** (**goose** approach): after a tool call/result pair ages past `cutoff` (configurable), a background `Task` replaces the raw pair with a one-sentence summary. This reclaims tokens from verbose shell output without an LLM-to-LLM call.

### 11.3 Compaction Prompt

```
You are a context summarisation assistant. Read the conversation below and produce a
structured checkpoint summary that another instance will use to continue the work.
Output ONLY the summary — do not continue the conversation.

## Goal
## Constraints & Preferences
## Progress
  ### Done
  ### In Progress
  ### Blocked
## Key Decisions
## Next Steps
## Critical Context
## Relevant Files

Rules:
- Use terse bullets, not prose paragraphs.
- Preserve exact file paths, method names, error strings, and identifiers.
- Do not mention the summary process or that context was compacted.
```

When updating an existing summary:
```
The messages below are new conversation turns to incorporate into the existing summary.
Preserve still-true details, remove stale details, and merge in new facts.
<previous_summary>
{previousSummary}
</previous_summary>
```

After compaction, inject a hidden continuation message into the session: *"Context was summarised. Continue naturally based on the summary — do not mention that summarisation occurred."*

### 11.4 BuildRequest — Per-Turn Pruning

`BuildRequest` is called before every LLM call. It assembles the full message list and prunes to fit:

1. Measure the **non-negotiable block**: system prompt + tool definitions + last user message + any pending tool results. If this alone exceeds `Usable`, throw `ContextTooSmallException` (user must shorten the system prompt or switch to a model with a larger context window).
2. Add messages from newest to oldest until the budget is consumed.
3. Flatten adjacent same-role messages.

### 11.5 Hard-Error Recovery

Provider-specific error detection:

| Provider | Error signal |
|----------|-------------|
| Anthropic | `error.type == "invalid_request_error"` + context keyword |
| OpenAI | `finish_reason == "length"` or HTTP 400 + "context_length_exceeded" |
| Ollama | HTTP 500 + "context length" in message |
| Azure | HTTP 400 + `code == "context_length_exceeded"` |

On detection: (1) run immediate compaction, (2) rebuild request, (3) retry once. If retry fails again, surface a clear error to the user in the conversation panel.

---

