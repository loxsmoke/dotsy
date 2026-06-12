using System.Text.Json;

namespace Dotsy.Core.Providers;

public record ChatRequest(
    string ModelId,
    string SystemPrompt,
    IReadOnlyList<Message> Messages,
    IReadOnlyList<ToolDefinition> Tools,
    int MaxTokens,
    float? Temperature = null);

public abstract record Message(string Role);

public record UserMessage(IReadOnlyList<ContentBlock> Content) : Message("user");

public record AssistantMessage(IReadOnlyList<ContentBlock> Content) : Message("assistant");

public abstract record ContentBlock(string Type);

public record TextBlock(string Text) : ContentBlock("text");

public record ThinkingBlock(string Thinking) : ContentBlock("thinking");

public record ToolUseBlock(string Id, string Name, JsonElement Input) : ContentBlock("tool_use");

public record ToolResultBlock(string ToolUseId, string Content, bool IsError = false)
    : ContentBlock("tool_result");

public record ToolDefinition(string Name, string Description, JsonElement InputSchema);
