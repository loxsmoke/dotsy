using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dotsy.Core.Providers;
using Dotsy.Core.Utils;

namespace Dotsy.Providers.Ollama;

public sealed class OllamaProvider : IProvider
{
    private readonly HttpClient _http;

    public string Name => "ollama";

    public OllamaProvider(string baseUrl = "http://localhost:11434", HttpClient? http = null)
    {
        // No timeout: local models can take arbitrarily long to generate responses.
        _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _http.BaseAddress = new Uri(baseUrl);
    }

    public async Task<ModelInfo> GetModelInfoAsync(string modelId, CancellationToken ct)
    {
        try
        {
            var resp = await _http.GetAsync("/api/tags", ct);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync(ct);
                var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("models", out var models))
                {
                    foreach (var m in models.EnumerateArray())
                    {
                        if (m.TryGetProperty("name", out var name) && name.GetString() == modelId)
                            return new ModelInfo(modelId, 128_000, 4_096);
                    }
                }
            }
        }
        catch { }

        return new ModelInfo(modelId, 128_000, 4_096);
    }

    public async IAsyncEnumerable<ProviderEvent> StreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var body = BuildRequestBody(request);
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
            yield return new StreamError(new ProviderException(
                new RequestError((int)resp.StatusCode, errBody)));
            yield break;
        }

        await foreach (var ev in ParseNdjsonStream(resp, ct))
            yield return ev;
    }

    private static string BuildRequestBody(ChatRequest request)
    {
        var obj = new JsonObject
        {
            ["model"] = request.ModelId,
            ["stream"] = true
        };

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

        var tcIds = new Dictionary<int, string>();
        var tcNames = new Dictionary<int, string>();
        var tcArgs = new Dictionary<int, StringBuilder>();
        var rawToolCalls = new RawToolCallParser();

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
                if (message.TryGetProperty("content", out var contentProp) &&
                    contentProp.ValueKind == JsonValueKind.String)
                {
                    var text = contentProp.GetString() ?? "";
                    if (text.Length > 0)
                    {
                        foreach (var ev in rawToolCalls.Process(text))
                            yield return ev;
                    }
                }

                if (message.TryGetProperty("tool_calls", out var toolCalls))
                {
                    int idx = 0;
                    foreach (var tc in toolCalls.EnumerateArray())
                    {
                        if (tc.TryGetProperty("function", out var func))
                        {
                            var id = $"ollama-{idx}";
                            var name = func.GetStringPropertyOrEmpty("name");
                            var args = func.TryGetProperty("arguments", out var a) ? a.GetRawText() : "{}";
                            yield return new ToolCallDelta(id, name, args);
                        }
                        idx++;
                    }
                }
            }

            if (done)
            {
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
}
