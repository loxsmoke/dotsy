## 6. AI Provider Layer

### 6.1 Abstraction

```csharp
// Core interface — all providers implement this
public interface IProvider
{
    string Name { get; }
    Task<ModelInfo> GetModelInfoAsync(string modelId, CancellationToken ct);
    IAsyncEnumerable<ProviderEvent> StreamAsync(ChatRequest request, CancellationToken ct);
}

public record ModelInfo(string Id, int ContextWindow, int MaxOutputTokens);

// Events emitted by StreamAsync
public abstract record ProviderEvent;
public record TextDelta(string Text)        : ProviderEvent;
public record ThinkingDelta(string Text)    : ProviderEvent;
public record ToolCallDelta(string Id, string Name, string ArgumentsJson) : ProviderEvent;
public record UsageUpdate(int InputTokens, int OutputTokens, int CacheReadTokens, int CacheWriteTokens) : ProviderEvent;
public record StreamEnd(StopReason Reason)  : ProviderEvent;
public record StreamError(Exception Ex)     : ProviderEvent;
```

### 6.2 ChatRequest

```csharp
public record ChatRequest(
    string ModelId,
    string SystemPrompt,
    IReadOnlyList<Message> Messages,
    IReadOnlyList<ToolDefinition> Tools,
    int MaxTokens,
    float? Temperature = null
);
```

### 6.3 Streaming

All providers must implement true streaming via SSE. The consumer (`AgentLoop`) drives an `await foreach` over `IAsyncEnumerable<ProviderEvent>`.

Anthropic and OpenAI differ in event format; each `IProvider` implementation owns the mapping to `ProviderEvent`. No shared parsing code — each provider is its own translation unit.

### 6.4 Token Counting

Always use the `UsageUpdate` event from the API response (exact token counts). Fall back to `EstimateTokens(text) = text.Length / 4` only when the provider does not report usage. Prefer the API's actual counts for compaction decisions (**pi** approach — more accurate than estimates).

### 6.5 Supported Providers at Launch

| Provider | Auth | Notes |
|----------|------|-------|
| Anthropic | API key | Claude 3.x + Claude 4.x; extended thinking support |
| OpenAI | API key | GPT-4o, GPT-4.1, o-series |
| Azure OpenAI | API key + endpoint | Per-deployment |
| Ollama | No auth | Local models via `POST /api/chat` |
| OpenAI-compatible | API key + base URL | OpenRouter, Together, DeepSeek, Mistral, etc. |

---

