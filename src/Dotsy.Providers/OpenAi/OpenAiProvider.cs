using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dotsy.Core.Config;
using Dotsy.Core.Providers;
using Dotsy.Core.Utils;

namespace Dotsy.Providers.OpenAi;

public class OpenAiProvider : IProvider
{
    protected readonly HttpClient Http;
    protected readonly string ApiKey;

    public virtual string Name => ProviderConfig.OpenAi;
    protected virtual string ChatEndpoint => "chat/completions";

    public OpenAiProvider(
        string apiKey,
        string baseUrl = "https://api.openai.com",
        HttpClient? http = null,
        bool normalizeOpenAiBaseUrl = true)
    {
        ApiKey = apiKey;
        // Infinite timeout: local/slow servers (koboldcpp, llama.cpp) can take minutes per turn;
        // the default 100s aborts mid-generation. Cancellation is handled via the request token.
        Http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        Http.BaseAddress = NormalizeBaseAddress(baseUrl, normalizeOpenAiBaseUrl);
        Http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ApiKey);
    }

    private static Uri NormalizeBaseAddress(string baseUrl, bool normalizeOpenAiBaseUrl)
    {
        var trimmed = string.IsNullOrWhiteSpace(baseUrl)
            ? "https://api.openai.com"
            : baseUrl.Trim();

        if (!normalizeOpenAiBaseUrl)
            return EnsureTrailingSlash(trimmed);

        var uri = EnsureTrailingSlash(trimmed);
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            return uri;

        return new Uri(uri, "v1/");
    }

    private static Uri EnsureTrailingSlash(string value) =>
        new(value.EndsWith('/') ? value : value + "/");

    // HARDCODED limits. The OpenAI-shaped /v1/models endpoint (used by OpenAI, Azure, and
    // OpenAI-compatible providers) does NOT report context-window or max-output token limits,
    // so there is no dynamic source — accurate values for known models come from ModelCatalog,
    // and unknown models get a conservative generic default. (Providers with a live source —
    // Anthropic, Gemini, Ollama — load these dynamically instead; see their overrides.)
    public virtual async Task<ModelInfo> GetModelInfoAsync(string modelId, CancellationToken ct)
    {
        if (ModelCatalog.TryLookup(modelId, out var known))
            return known;

        try
        {
            var resp = await Http.GetAsync("models", ct);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync(ct);
                var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("data", out var data))
                {
                    foreach (var m in data.EnumerateArray())
                    {
                        if (m.TryGetProperty("id", out var id) && id.GetString() == modelId)
                            return new ModelInfo(modelId, 128_000, 16_384);
                    }
                }
            }
        }
        catch { }

        return new ModelInfo(modelId, 128_000, 4_096);
    }

    // The OpenAI-shaped /v1/models endpoint lists available model ids but reports no token
    // limits, so fill those from ModelCatalog when known and fall back to a generic default.
    public virtual async Task<IReadOnlyList<ModelInfo>> GetModelsAsync(CancellationToken ct)
    {
        try
        {
            var resp = await Http.GetAsync("models", ct);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync(ct);
                var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("data", out var data))
                {
                    return data.EnumerateArray()
                        .Select(m => m.GetStringPropertyOrEmpty("id"))
                        .Where(id => !string.IsNullOrEmpty(id))
                        .Select(id => ModelCatalog.TryLookup(id, out var known)
                            ? known
                            : new ModelInfo(id, 128_000, 4_096))
                        .ToList();
                }
            }
        }
        catch { }

        return [];
    }

    public async IAsyncEnumerable<ProviderEvent> StreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var body = BuildRequestBody(request);
        var content = new StringContent(body, Encoding.UTF8, "application/json");

        var post = await ProviderHttp.PostAsync(Http, ChatEndpoint, content, ct);
        if (post.Error is not null)
        {
            yield return post.Error;
            yield break;
        }

        if (!post.Response!.IsSuccessStatusCode)
        {
            await foreach (var ev in HandleErrorResponse(post.Response, ct))
                yield return ev;
            yield break;
        }

        await foreach (var ev in ParseSseStream(post.Response, ct))
            yield return ev;
    }

    protected virtual string BuildRequestBody(ChatRequest request)
    {
        var obj = new JsonObject
        {
            ["model"] = request.ModelId,
            ["max_tokens"] = request.MaxTokens,
            ["stream"] = true,
            ["stream_options"] = new JsonObject { ["include_usage"] = true }
        };

        if (request.Temperature.HasValue)
            obj["temperature"] = request.Temperature.Value;

        if (!string.IsNullOrEmpty(request.SystemPrompt))
        {
            var messages = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = request.SystemPrompt }
            };
            foreach (var msg in request.Messages)
                foreach (var converted in ConvertMessage(msg))
                    messages.Add(converted);
            obj["messages"] = messages;
        }
        else
        {
            var messages = new JsonArray();
            foreach (var msg in request.Messages)
                foreach (var converted in ConvertMessage(msg))
                    messages.Add(converted);
            obj["messages"] = messages;
        }

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
        IReadOnlyList<ContentBlock> blocks = msg switch
        {
            UserMessage u => u.Content,
            AssistantMessage a => a.Content,
            _ => []
        };

        // Flatten tool_use blocks into tool_calls array for assistant messages
        if (msg is AssistantMessage)
        {
            var textParts = blocks.OfType<TextBlock>().Select(b => b.Text);
            var textContent = string.Join("", textParts);
            var toolUseBlocks = blocks.OfType<ToolUseBlock>().ToList();

            var obj = new JsonObject { ["role"] = "assistant" };
            if (!string.IsNullOrEmpty(textContent))
                obj["content"] = textContent;

            if (toolUseBlocks.Count > 0)
            {
                var toolCalls = new JsonArray();
                foreach (var tu in toolUseBlocks)
                {
                    toolCalls.Add(new JsonObject
                    {
                        ["id"] = tu.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = tu.Name,
                            ["arguments"] = tu.Input.GetRawText()
                        }
                    });
                }
                obj["tool_calls"] = toolCalls;
            }
            yield return obj;
            yield break;
        }

        // User message: emit one tool message per tool_result block. Parallel tool calls
        // produce multiple results in a single message, and OpenAI requires a response for
        // every tool_call_id — emitting only the first triggers a 400 ("did not have response").
        var toolResults = blocks.OfType<ToolResultBlock>().ToList();
        if (toolResults.Count > 0)
        {
            foreach (var tr in toolResults)
            {
                yield return new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = tr.ToolUseId,
                    ["content"] = tr.Content
                };
            }
            yield break;
        }

        // Plain user message
        var textBlocks = blocks.OfType<TextBlock>().ToList();
        var userText = string.Join("", textBlocks.Select(b => b.Text));
        yield return new JsonObject { ["role"] = "user", ["content"] = userText };
    }

    private static async IAsyncEnumerable<ProviderEvent> ParseSseStream(
        HttpResponseMessage resp,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        // Accumulate tool call arguments per index
        var tcIds = new Dictionary<int, string>();
        var tcNames = new Dictionary<int, string>();
        var tcArgs = new Dictionary<int, StringBuilder>();

        // Some models inline reasoning as <think>…</think> in content instead of a
        // dedicated delta field; split it out so it renders as thinking.
        var thinkParser = new ThinkTagParser();

        // Usage placement varies by server: a dedicated final chunk without "choices", the
        // OpenAI-spec include_usage chunk with an empty choices array, or riding on the last
        // content chunk. Some servers also send cumulative usage on every chunk. So capture the
        // last value seen anywhere and emit a single UsageUpdate when the stream ends.
        int usageInput = 0, usageOutput = 0;
        bool sawUsage = false;
        long? serverDurationMs = null;

        while (!ct.IsCancellationRequested)
        {
            var (line, readError) = await ProviderHttp.ReadSseLineAsync(reader, ct);
            if (readError is not null)
            {
                yield return readError;
                yield break;
            }
            if (line is null)
                break;
            if (!line.StartsWith("data: "))
                continue;

            var data = line[6..];
            if (data == "[DONE]")
                break;

            JsonElement chunk;
            try { chunk = JsonDocument.Parse(data).RootElement; }
            catch { continue; }

            if (chunk.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                if (usage.TryGetProperty("prompt_tokens", out var pt) && pt.TryGetInt32(out var ptv))
                { usageInput = ptv; sawUsage = true; }
                if (usage.TryGetProperty("completion_tokens", out var ct2) && ct2.TryGetInt32(out var ctv))
                { usageOutput = ctv; sawUsage = true; }
            }

            // Non-standard llama.cpp-family extension: "timings.predicted_ms" is generation time
            // measured by the server itself — the same signal as Ollama's eval_duration.
            if (chunk.TryGetProperty("timings", out var timings)
                && timings.ValueKind == JsonValueKind.Object
                && timings.TryGetProperty("predicted_ms", out var pms)
                && pms.TryGetDouble(out var predictedMs) && predictedMs > 0)
            {
                serverDurationMs = (long)predictedMs;
            }

            if (!chunk.TryGetProperty("choices", out var choices))
                continue;

            if (choices.GetArrayLength() == 0)
                continue;

            var choice = choices[0];
            string? finishReason = choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind != JsonValueKind.Null
                ? fr.GetString() : null;

            if (!choice.TryGetProperty("delta", out var delta))
                continue;

            // Reasoning delta fields: "reasoning" (OpenRouter) / "reasoning_content"
            // (DeepSeek, vLLM, LM Studio, and other compatible servers).
            foreach (var reasoningField in (string[])["reasoning", "reasoning_content"])
            {
                if (delta.TryGetProperty(reasoningField, out var reasoningProp)
                    && reasoningProp.ValueKind == JsonValueKind.String)
                {
                    var reasoning = reasoningProp.GetString() ?? "";
                    if (reasoning.Length > 0)
                        yield return new ThinkingDelta(reasoning);
                }
            }

            // Text content
            if (delta.TryGetProperty("content", out var contentProp) && contentProp.ValueKind == JsonValueKind.String)
            {
                var text = contentProp.GetString() ?? "";
                if (text.Length > 0)
                {
                    foreach (var seg in thinkParser.Process(text))
                    {
                        if (seg.IsThinking)
                            yield return new ThinkingDelta(seg.Text);
                        else
                            yield return new TextDelta(seg.Text);
                    }
                }
            }

            // Tool calls deltas
            if (delta.TryGetProperty("tool_calls", out var toolCallsArr))
            {
                foreach (var tc in toolCallsArr.EnumerateArray())
                {
                    int idx = tc.TryGetProperty("index", out var i) ? i.GetInt32() : 0;

                    if (tc.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
                        tcIds[idx] = idProp.GetString() ?? "";

                    if (tc.TryGetProperty("function", out var func))
                    {
                        if (func.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
                            tcNames[idx] = nameProp.GetString() ?? "";

                        if (func.TryGetProperty("arguments", out var argsProp) && argsProp.ValueKind == JsonValueKind.String)
                        {
                            if (!tcArgs.ContainsKey(idx))
                                tcArgs[idx] = new StringBuilder();
                            tcArgs[idx].Append(argsProp.GetString());
                        }
                    }
                }
            }

            // On finish, emit accumulated tool calls
            if (finishReason is not null)
            {
                foreach (var seg in thinkParser.Complete())
                {
                    if (seg.IsThinking)
                        yield return new ThinkingDelta(seg.Text);
                    else
                        yield return new TextDelta(seg.Text);
                }

                foreach (var kv in tcArgs)
                {
                    var id = tcIds.GetValueOrDefault(kv.Key, "");
                    var name = tcNames.GetValueOrDefault(kv.Key, "");
                    yield return new ToolCallDelta(id, name, kv.Value.ToString());
                }
                tcIds.Clear(); tcNames.Clear(); tcArgs.Clear();

                // "length" means the max_tokens output cap was reached, NOT a context overflow —
                // a genuine overflow arrives as an HTTP 400 handled in HandleErrorResponse. Map it
                // to StopReason.MaxTokens so the loop's truncated-output handling deals with it.
                var reason = finishReason switch
                {
                    "stop" => StopReason.EndTurn,
                    "tool_calls" => StopReason.ToolUse,
                    "length" => StopReason.MaxTokens,
                    _ => StopReason.EndTurn
                };

                yield return new StreamEnd(reason);
            }
        }

        if (sawUsage)
            yield return new UsageUpdate(usageInput, usageOutput, 0, 0, serverDurationMs);
    }

    private static (string Type, string Message, string RequestId) ParseErrorBody(
        string body, HttpResponseMessage resp)
    {
        var reqId = resp.Headers.TryGetValues("x-request-id", out var ids)
            ? ids.FirstOrDefault() ?? ""
            : "";
        try
        {
            var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                var type = err.GetStringPropertyOrEmpty("type");
                var msg  = err.GetStringPropertyOrEmpty("message");
                return (type, msg, reqId);
            }
        }
        catch { }
        return ("", body, reqId);
    }

    private static async IAsyncEnumerable<ProviderEvent> HandleErrorResponse(
        HttpResponseMessage resp,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var body = await resp.Content.ReadAsStringAsync(ct);
        var status = (int)resp.StatusCode;

        ProviderError error;
        if (status == 401 || status == 403)
        {
            var (errType, errMsg, reqId) = ParseErrorBody(body, resp);
            var detail = string.IsNullOrEmpty(errType) ? body : errType;
            error = new AuthError($"{detail}\nMessage: {errMsg}\nRequest ID: {reqId}".TrimEnd());
        }
        else if (ProviderHttp.TryClassifyCommonError(resp, out var commonError))
            error = commonError!;
        else
        {
            var (errType, errMsg, reqId) = ParseErrorBody(body, resp);
            if (!string.IsNullOrEmpty(errMsg)
                && errMsg.ContainsNoCase("context")
                && errMsg.ContainsNoCase("length"))
            {
                error = new ContextLengthError(BuildDetail(errMsg, errType, body, reqId));
            }
            else if (IsModelUnknownError(errMsg) || IsModelUnknownError(errType))
            {
                error = new ModelUnknownError(BuildDetail(errMsg, errType, body, reqId));
            }
            else
            {
                error = new RequestError(status, BuildDetail(errMsg, errType, body, reqId));
            }
        }

        yield return new StreamError(new ProviderException(error));
    }

    // Prefer the API's human-readable message; fall back to the error type or raw body.
    private static string BuildDetail(string message, string type, string body, string requestId)
    {
        var detail = !string.IsNullOrEmpty(message) ? message
                   : !string.IsNullOrEmpty(type)    ? type
                   : body.Trim();
        if (!string.IsNullOrEmpty(requestId))
            detail += $" (request {requestId})";
        return detail;
    }

    private static bool IsModelUnknownError(string value) =>
        !string.IsNullOrEmpty(value)
        && value.ContainsNoCase("model")
        && (value.ContainsNoCase("unknown")
            || value.ContainsNoCase("not found")
            || value.ContainsNoCase("invalid"));
}
