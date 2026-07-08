## 21. Prompt Caching

### 21.1 Supported Providers

| Provider | Cache API | Notes |
|----------|-----------|-------|
| Anthropic | `cache_control: { type: "ephemeral" }` | 5-min TTL (API key); 1-h TTL (extended, opt-in) |
| OpenAI | Automatic prefix caching | No explicit breakpoints needed; handled server-side |
| Azure OpenAI | Automatic prefix caching | Same as OpenAI |
| Ollama | Not supported | Local models have no caching |
| OpenAI-compatible | Automatic (if provider supports) | Pass-through; no explicit markers |

### 21.2 Cache Breakpoint Placement

For Anthropic, `cache_control` markers are placed by `SystemPromptBuilder.Build()` in this order (matching the **pi** layered-prefix strategy):

1. **End of static system prompt prefix** — identity, principles, tool policy (never changes between turns → warm on every call)
2. **End of project context files** — AGENTS.md etc. (changes only when files change → usually warm)
3. **End of repo map block** — updated each turn but stable within a session (warm after first turn)
4. **Most recent user message** — catches the growing conversation tail

Anthropic allows up to 4 active breakpoints; this uses all 4. Tool definitions are in the `tools` array and are cached automatically on Anthropic (they are included in the cacheable prefix).

The static prefix is placed first so that all users sharing the same binary version hit the same cached prefix — the **pi** approach that drives significant cache savings across concurrent users.

### 21.3 Cache Token Tracking

Cache read and write tokens are returned by the `UsageUpdate` provider event (§6.4) and tracked separately from regular input/output tokens:

```csharp
public record UsageUpdate(
    int InputTokens,
    int OutputTokens,
    int CacheReadTokens,    // cheap: ~0.1× input price
    int CacheWriteTokens);  // slight premium: ~1.25× input price
```

The loop republishes these as `TokenUsageUpdated` loop events (§7.3). Cache read and write tokens are stored per-line in the session JSONL `usage` object (§13.1).
