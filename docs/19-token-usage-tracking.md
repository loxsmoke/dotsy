## 19. Token Usage Tracking

### 19.1 Usage Events

Providers emit exact token counts via `UsageUpdate` when available:

```csharp
public record UsageUpdate(
    int InputTokens,
    int OutputTokens,
    int CacheReadTokens,
    int CacheWriteTokens
);
```

The loop forwards usage as `TokenUsageUpdated` events and uses input/output totals for context-budget tracking.

### 19.2 Per-Turn Display

After each assistant turn the status bar updates:

```
claude-opus-4-7  ·  [42% ctx]
```

Context usage is derived from provider-reported input/output token counts.

### 19.3 Headless Output

The final `--output-format json` object includes accumulated `inputTokens` and `outputTokens`.

---

