using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dotsy.Core.Config;
using Dotsy.Core.Providers;
using Dotsy.Core.Utils;

namespace Dotsy.Providers.Ollama;

public sealed class OllamaProvider : IProvider
{
    private readonly HttpClient _http;
    private readonly int _maxContextTokens;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _thinkingCapable = new();

    public string Name => ProviderConfig.Ollama;

    public OllamaProvider(string baseUrl = "http://localhost:11434", HttpClient? http = null, int maxContextTokens = 0)
    {
        // No timeout: local models can take arbitrarily long to generate responses.
        _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _http.BaseAddress = new Uri(baseUrl);
        _maxContextTokens = maxContextTokens;
    }

    // DYNAMIC limits. We want the context window the model is ACTUALLY running with (num_ctx),
    // not the architecture's maximum. Ollama loads a model at a modest default (e.g. 8192) unless
    // told otherwise, so the two differ widely — a model capable of 256K is typically served at 8K.
    //   1. If max_context_tokens is configured we send it as num_ctx on every chat call, so the
    //      model runs with exactly that. It is the authoritative active window — for both the /model
    //      display and the token budget — so it wins outright.
    //   2. Otherwise Ollama picks the window: /api/ps reports the live num_ctx of each loaded model
    //      (what `ollama ps` shows), so prefer it to match what generation actually honors.
    //   3. If the model isn't loaded yet, fall back to /api/show's architecture context length
    //      (the capability ceiling, flagged Advertised) as a best-effort estimate.
    //   4. Failing all, a generic default. Ollama reports no max-output limit, so that stays fixed.
    public async Task<ModelInfo> GetModelInfoAsync(string modelId, CancellationToken ct)
    {
        if (_maxContextTokens > 0)
            return new ModelInfo(modelId, _maxContextTokens, 4_096, ModelInfoSource.Api);

        var loadedCtx = await TryGetLoadedContextLengthAsync(modelId, ct);
        if (loadedCtx is > 0)
            return new ModelInfo(modelId, loadedCtx.Value, 4_096, ModelInfoSource.Api);

        try
        {
            var requestBody = new JsonObject { ["model"] = modelId }.ToJsonString();
            var content = new StringContent(requestBody, Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync("/api/show", content, ct);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync(ct);
                var root = JsonDocument.Parse(json).RootElement;
                if (root.TryGetProperty("model_info", out var info) && info.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in info.EnumerateObject())
                    {
                        if (prop.Name.EndsWith(".context_length", StringComparison.Ordinal)
                            && prop.Value.TryGetInt32(out var ctxLen) && ctxLen > 0)
                            return new ModelInfo(modelId, ctxLen, 4_096, ModelInfoSource.Advertised);
                    }
                }
            }
        }
        catch { }

        return new ModelInfo(modelId, 128_000, 4_096);
    }

    // The context window (num_ctx) the model is currently loaded with, via /api/ps — the same
    // figure `ollama ps` prints under CONTEXT. Returns null when the model isn't running (nothing
    // is loaded until the first request) or the running Ollama is too old to report context_length.
    private async Task<int?> TryGetLoadedContextLengthAsync(string modelId, CancellationToken ct)
    {
        try
        {
            var resp = await _http.GetAsync("/api/ps", ct);
            if (!resp.IsSuccessStatusCode)
                return null;

            var json = await resp.Content.ReadAsStringAsync(ct);
            var root = JsonDocument.Parse(json).RootElement;
            if (!root.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var m in models.EnumerateArray())
            {
                if (!ModelNameMatches(m.GetStringPropertyOrEmpty("name"), modelId)
                    && !ModelNameMatches(m.GetStringPropertyOrEmpty("model"), modelId))
                    continue;

                if (m.TryGetProperty("context_length", out var ctxLen)
                    && ctxLen.TryGetInt32(out var n) && n > 0)
                    return n;
            }
        }
        catch { }

        return null;
    }

    // Matches a loaded model name against the configured id, tolerating an implicit ":latest" tag
    // on either side (config may say "qwen3" while /api/ps reports "qwen3:latest").
    private static bool ModelNameMatches(string loaded, string requested)
    {
        if (string.IsNullOrEmpty(loaded) || string.IsNullOrEmpty(requested))
            return false;
        if (string.Equals(loaded, requested, StringComparison.Ordinal))
            return true;

        static string StripLatest(string s) =>
            s.EndsWith(":latest", StringComparison.Ordinal) ? s[..^":latest".Length] : s;
        return string.Equals(StripLatest(loaded), StripLatest(requested), StringComparison.Ordinal);
    }

    // Lists locally-installed models via /api/tags. That endpoint reports names but not context
    // length (which lives in /api/show per model), so report a generic default here; callers that
    // need accurate limits resolve them via GetModelInfoAsync.
    public async Task<IReadOnlyList<ModelInfo>> GetModelsAsync(CancellationToken ct)
    {
        try
        {
            var resp = await _http.GetAsync("/api/tags", ct);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync(ct);
                var root = JsonDocument.Parse(json).RootElement;
                if (root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
                {
                    return models.EnumerateArray()
                        .Select(m => m.GetStringPropertyOrEmpty("name"))
                        .Where(name => !string.IsNullOrEmpty(name))
                        .Select(name => new ModelInfo(name, 128_000, 4_096, ModelInfoSource.Api))
                        .ToList();
                }
            }
        }
        catch { }

        return [];
    }

    // Whether the model advertises the "thinking" capability via /api/show. Sending
    // think:true to a model that lacks it returns a 400, so we must check first.
    // Cached per model since capabilities don't change within a process.
    private async Task<bool> SupportsThinkingAsync(string modelId, CancellationToken ct)
    {
        if (_thinkingCapable.TryGetValue(modelId, out var cached))
            return cached;

        bool supported = false;
        try
        {
            var requestBody = new JsonObject { ["model"] = modelId }.ToJsonString();
            var content = new StringContent(requestBody, Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync("/api/show", content, ct);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync(ct);
                var root = JsonDocument.Parse(json).RootElement;
                if (root.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Array)
                {
                    foreach (var cap in caps.EnumerateArray())
                    {
                        if (cap.ValueKind == JsonValueKind.String &&
                            string.Equals(cap.GetString(), "thinking", StringComparison.Ordinal))
                        {
                            supported = true;
                            break;
                        }
                    }
                }
            }
        }
        catch { }

        _thinkingCapable[modelId] = supported;
        return supported;
    }

    public async IAsyncEnumerable<ProviderEvent> StreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Ask thinking-capable models to emit reasoning in a separate `thinking` field.
        var think = await SupportsThinkingAsync(request.ModelId, ct);
        var body = BuildRequestBody(request, think, _maxContextTokens);
        var content = new StringContent(body, Encoding.UTF8, "application/json");

        HttpResponseMessage? resp = null;
        Exception? networkEx = null;
        try
        {
            resp = await _http.PostAsync("/api/chat", content, ct);
        }
        catch (Exception ex)
        {
            networkEx = ex;
        }

        if (networkEx is not null)
        {
            yield return new StreamError(new ProviderException(new NetworkError(networkEx)));
            yield break;
        }

        if (!resp!.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync(ct);
            var detail = errBody.Trim();
            try
            {
                var doc = JsonDocument.Parse(errBody);
                if (doc.RootElement.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
                    detail = err.GetString() ?? detail;
            }
            catch { }
            ProviderError error = IsModelUnknownError(detail)
                ? new ModelUnknownError(detail)
                : new RequestError((int)resp.StatusCode, detail);
            yield return new StreamError(new ProviderException(error));
            yield break;
        }

        await foreach (var ev in ParseNdjsonStream(resp, ct))
            yield return ev;
    }

    private static string BuildRequestBody(ChatRequest request, bool think, int maxContextTokens)
    {
        var obj = new JsonObject
        {
            ["model"] = request.ModelId,
            ["stream"] = true
        };
        if (think)
            obj["think"] = true;

        // Size the context window the model is loaded/run with. Without num_ctx, Ollama uses a small
        // server default (often 8192) regardless of the model's capacity, silently truncating input.
        if (maxContextTokens > 0)
            obj["options"] = new JsonObject { ["num_ctx"] = maxContextTokens };

        var messages = new JsonArray();
        if (!string.IsNullOrEmpty(request.SystemPrompt))
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = request.SystemPrompt });

        foreach (var msg in request.Messages)
        {
            foreach (var converted in ConvertMessage(msg))
                messages.Add(converted);
        }
        obj["messages"] = messages;

        if (request.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var t in request.Tools)
            {
                tools.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = t.Name,
                        ["description"] = t.Description,
                        ["parameters"] = JsonNode.Parse(t.InputSchema.GetRawText())
                    }
                });
            }
            obj["tools"] = tools;
        }

        return obj.ToJsonString();
    }

    private static bool IsModelUnknownError(string value) =>
        !string.IsNullOrEmpty(value)
        && value.ContainsNoCase("model")
        && (value.ContainsNoCase("unknown")
            || value.ContainsNoCase("not found")
            || value.ContainsNoCase("invalid"));

    private static IEnumerable<JsonObject> ConvertMessage(Message msg)
    {
        if (msg is AssistantMessage assistantMsg)
        {
            var textBlocks = assistantMsg.Content.OfType<TextBlock>().ToList();
            var toolUses = assistantMsg.Content.OfType<ToolUseBlock>().ToList();

            var msgObj = new JsonObject
            {
                ["role"] = "assistant",
                // content must always be present; empty string when only tool calls
                ["content"] = textBlocks.Count > 0
                    ? string.Join("", textBlocks.Select(b => b.Text))
                    : ""
            };

            if (toolUses.Count > 0)
            {
                var toolCallsArr = new JsonArray();
                foreach (var tu in toolUses)
                {
                    // Ollama format: no id/type wrapper, arguments must be a JSON object (not a string)
                    toolCallsArr.Add(new JsonObject
                    {
                        ["function"] = new JsonObject
                        {
                            ["name"] = tu.Name,
                            ["arguments"] = JsonNode.Parse(tu.Input.GetRawText())
                        }
                    });
                }
                msgObj["tool_calls"] = toolCallsArr;
            }

            yield return msgObj;
            yield break;
        }

        if (msg is UserMessage userMsg)
        {
            var toolResults = userMsg.Content.OfType<ToolResultBlock>().ToList();
            var textBlocks = userMsg.Content.OfType<TextBlock>().ToList();

            // Each tool result becomes its own role:"tool" message (Ollama doesn't use tool_call_id)
            foreach (var tr in toolResults)
                yield return new JsonObject { ["role"] = "tool", ["content"] = tr.Content };

            if (textBlocks.Count > 0)
                yield return new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = string.Join("", textBlocks.Select(b => b.Text))
                };
        }
    }

    private static async IAsyncEnumerable<ProviderEvent> ParseNdjsonStream(
        HttpResponseMessage resp,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        var rawToolCalls = new RawToolCallParser();
        var thinkParser = new ThinkTagParser();
        var toolCallIdx = 0;

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line))
                continue;

            JsonElement chunk;
            try { chunk = JsonDocument.Parse(line).RootElement; }
            catch { continue; }

            bool done = chunk.TryGetProperty("done", out var d) && d.GetBoolean();

            if (chunk.TryGetProperty("message", out var message))
            {
                // Native reasoning field, emitted when think:true and the model supports it.
                if (message.TryGetProperty("thinking", out var thinkingProp) &&
                    thinkingProp.ValueKind == JsonValueKind.String)
                {
                    var reasoning = thinkingProp.GetString() ?? "";
                    if (reasoning.Length > 0)
                        yield return new ThinkingDelta(reasoning);
                }

                if (message.TryGetProperty("content", out var contentProp) &&
                    contentProp.ValueKind == JsonValueKind.String)
                {
                    var text = contentProp.GetString() ?? "";
                    if (text.Length > 0)
                    {
                        // Some models inline reasoning as <think>…</think> in content rather than
                        // the dedicated field; split it out so it renders as thinking, and run the
                        // remaining answer text through the tool-call parser.
                        foreach (var seg in thinkParser.Process(text))
                        {
                            if (seg.IsThinking)
                                yield return new ThinkingDelta(seg.Text);
                            else
                                foreach (var ev in rawToolCalls.Process(seg.Text))
                                    yield return ev;
                        }
                    }
                }

                if (message.TryGetProperty("tool_calls", out var toolCalls))
                {
                    foreach (var tc in toolCalls.EnumerateArray())
                    {
                        if (tc.TryGetProperty("function", out var func))
                        {
                            var id = $"ollama-{toolCallIdx}";
                            var name = func.GetStringPropertyOrEmpty("name");
                            var args = func.TryGetProperty("arguments", out var a) ? a.GetRawText() : "{}";
                            yield return new ToolCallDelta(id, name, args);
                        }
                        toolCallIdx++;
                    }
                }
            }

            if (done)
            {
                foreach (var seg in thinkParser.Complete())
                {
                    if (seg.IsThinking)
                        yield return new ThinkingDelta(seg.Text);
                    else
                        foreach (var ev in rawToolCalls.Process(seg.Text))
                            yield return ev;
                }
                foreach (var ev in rawToolCalls.Complete())
                    yield return ev;
                if (chunk.TryGetProperty("eval_count", out var evalCount))
                {
                    int promptTokens = chunk.TryGetProperty("prompt_eval_count", out var pec) ? pec.GetInt32() : 0;
                    yield return new UsageUpdate(promptTokens, evalCount.GetInt32(), 0, 0);
                }
                yield return new StreamEnd(StopReason.EndTurn);
                yield break;
            }
        }
    }

    private sealed class RawToolCallParser
    {
        private const string OpenTag = "<tool_call>";
        private const string FunctionTag = "<function=";
        private const string CloseTag = "</tool_call>";
        private int _nextId = 1;
        private readonly StringBuilder _buffer = new();

        public IEnumerable<ProviderEvent> Process(string text)
        {
            _buffer.Append(text);

            while (_buffer.Length > 0)
            {
                var current = _buffer.ToString();
                var start = FindToolStart(current);
                if (start < 0)
                {
                    var keep = Math.Max(
                        LongestSuffixThatStarts(OpenTag, current),
                        LongestSuffixThatStarts(FunctionTag, current));
                    var emitLength = current.Length - keep;
                    if (emitLength > 0)
                    {
                        yield return new TextDelta(current[..emitLength]);
                        _buffer.Remove(0, emitLength);
                    }
                    yield break;
                }

                if (start > 0)
                {
                    yield return new TextDelta(current[..start]);
                    _buffer.Remove(0, start);
                    continue;
                }

                var end = current.IndexOf(CloseTag, start, StringComparison.Ordinal);
                if (end < 0)
                    yield break;

                var blockLength = end + CloseTag.Length;
                var block = current[..blockLength];
                _buffer.Remove(0, blockLength);

                if (TryParse(block, $"ollama-raw-{_nextId++}", out var toolCall))
                    yield return toolCall;
                else
                    yield return new TextDelta(block);
            }
        }

        public IEnumerable<ProviderEvent> Complete()
        {
            if (_buffer.Length == 0)
                yield break;

            var text = _buffer.ToString();
            _buffer.Clear();
            yield return new TextDelta(text);
        }

        private static int LongestSuffixThatStarts(string prefix, string text)
        {
            var max = Math.Min(prefix.Length - 1, text.Length);
            for (var len = max; len > 0; len--)
            {
                if (text.AsSpan(text.Length - len, len).SequenceEqual(prefix.AsSpan(0, len)))
                    return len;
            }
            return 0;
        }

        private static int FindToolStart(string text)
        {
            var openStart = text.IndexOf(OpenTag, StringComparison.Ordinal);
            var functionStart = text.IndexOf(FunctionTag, StringComparison.Ordinal);

            if (openStart < 0)
                return functionStart;
            if (functionStart < 0)
                return openStart;
            return Math.Min(openStart, functionStart);
        }

        private static bool TryParse(string block, string id, out ToolCallDelta toolCall)
        {
            toolCall = default!;

            var functionStart = block.IndexOf("<function=", StringComparison.Ordinal);
            if (functionStart < 0)
                return false;

            var nameStart = functionStart + "<function=".Length;
            var nameEnd = block.IndexOf('>', nameStart);
            if (nameEnd <= nameStart)
                return false;

            var name = block[nameStart..nameEnd].Trim();
            if (string.IsNullOrWhiteSpace(name))
                return false;

            var args = new JsonObject();
            var pos = nameEnd + 1;
            while (true)
            {
                var paramStart = block.IndexOf("<parameter=", pos, StringComparison.Ordinal);
                if (paramStart < 0)
                    break;

                var paramNameStart = paramStart + "<parameter=".Length;
                var paramNameEnd = block.IndexOf('>', paramNameStart);
                if (paramNameEnd <= paramNameStart)
                    return false;

                var close = block.IndexOf("</parameter>", paramNameEnd + 1, StringComparison.Ordinal);
                if (close < 0)
                    return false;

                var paramName = block[paramNameStart..paramNameEnd].Trim();
                if (!string.IsNullOrEmpty(paramName))
                    args[paramName] = ParseParameterValue(block[(paramNameEnd + 1)..close].Trim('\r', '\n'));
                pos = close + "</parameter>".Length;
            }

            toolCall = new ToolCallDelta(id, name, args.ToJsonString());
            return true;
        }

        private static JsonNode ParseParameterValue(string value)
        {
            try
            {
                return JsonNode.Parse(value) ?? JsonValue.Create(value)!;
            }
            catch (JsonException)
            {
                return JsonValue.Create(value)!;
            }
        }
    }

    // Splits a streamed content string into plain-text and <think>…</think> reasoning
    // segments, tolerating tags that straddle chunk boundaries (a trailing partial tag is
    // held back until the next chunk completes or disproves it).
    private sealed class ThinkTagParser
    {
        private const string OpenTag = "<think>";
        private const string CloseTag = "</think>";
        private readonly StringBuilder _buffer = new();
        private bool _inThink;

        public readonly record struct Segment(bool IsThinking, string Text);

        public IEnumerable<Segment> Process(string text)
        {
            _buffer.Append(text);

            while (_buffer.Length > 0)
            {
                var current = _buffer.ToString();
                var (tag, isThinking) = _inThink ? (CloseTag, true) : (OpenTag, false);
                var at = current.IndexOf(tag, StringComparison.Ordinal);

                if (at < 0)
                {
                    // No complete tag. Emit everything except a possible partial tag at the end.
                    var keep = LongestSuffixThatStarts(tag, current);
                    var emit = current.Length - keep;
                    if (emit > 0)
                    {
                        yield return new Segment(isThinking, current[..emit]);
                        _buffer.Remove(0, emit);
                    }
                    yield break;
                }

                if (at > 0)
                    yield return new Segment(isThinking, current[..at]);
                _buffer.Remove(0, at + tag.Length);
                _inThink = !_inThink;
            }
        }

        public IEnumerable<Segment> Complete()
        {
            if (_buffer.Length == 0)
                yield break;
            var text = _buffer.ToString();
            _buffer.Clear();
            yield return new Segment(_inThink, text);
        }

        private static int LongestSuffixThatStarts(string prefix, string text)
        {
            var max = Math.Min(prefix.Length - 1, text.Length);
            for (var len = max; len > 0; len--)
            {
                if (text.AsSpan(text.Length - len, len).SequenceEqual(prefix.AsSpan(0, len)))
                    return len;
            }
            return 0;
        }
    }
}
