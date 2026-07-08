using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dotsy.Core.Config;
using Dotsy.Core.Providers;
using Dotsy.Core.Utils;

namespace Dotsy.Providers.Anthropic;

public sealed class AnthropicProvider : IProvider
{
    private const string ApiVersion = "2023-06-01";
    private const string BaseUrl = "https://api.anthropic.com";

    private static readonly IReadOnlyList<ModelInfo> BundledModels =
    [
        new("claude-opus-4-7",    200_000, 32_000),
        new("claude-sonnet-4-6",  200_000, 64_000),
        new("claude-haiku-4-5-20251001", 200_000, 16_000),
        new("claude-opus-4-5",    200_000, 32_000),
    ];

    private readonly HttpClient _http;
    private readonly string _apiKey;

    public string Name => ProviderConfig.Anthropic;

    public AnthropicProvider(string apiKey, HttpClient? http = null)
    {
        _apiKey = apiKey;
        _http = http ?? new HttpClient();
        _http.BaseAddress = new Uri(BaseUrl);
        _http.DefaultRequestHeaders.Add("x-api-key", _apiKey);
        _http.DefaultRequestHeaders.Add("anthropic-version", ApiVersion);
        _http.DefaultRequestHeaders.Add("anthropic-beta", "interleaved-thinking-2025-05-14");
    }

    // DYNAMIC limits. Anthropic's /v1/models endpoint reports real context_window /
    // max_output_tokens, so load them live. Falls back to a conservative default if the
    // model isn't listed or the call fails.
    public async Task<ModelInfo> GetModelInfoAsync(string modelId, CancellationToken ct)
    {
        try
        {
            var models = await GetModelsAsync(ct);
            var m = models.FirstOrDefault(x => x.Id == modelId);
            if (m is not null) return m;
        }
        catch { }

        return new ModelInfo(modelId, 200_000, 8_192);
    }

    public async Task<IReadOnlyList<ModelInfo>> GetModelsAsync(CancellationToken ct)
    {
        try
        {
            var resp = await _http.GetAsync("/v1/models", ct);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync(ct);
                var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("data", out var data))
                {
                    return data.EnumerateArray().Select(m => new ModelInfo(
                        m.GetStringPropertyOrEmpty("id"),
                        m.TryGetProperty("context_window", out var cw) ? cw.GetInt32() : 200_000,
                        m.TryGetProperty("max_output_tokens", out var mo) ? mo.GetInt32() : 8_192,
                        ModelInfoSource.Api)).ToList();
                }
            }
        }
        catch { }

        return BundledModels;
    }

    public async IAsyncEnumerable<ProviderEvent> StreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var body = BuildRequestBody(request);
        var content = new StringContent(body, Encoding.UTF8, "application/json");

        var post = await ProviderHttp.PostAsync(_http, "/v1/messages", content, ct);
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

    private static string BuildRequestBody(ChatRequest request)
    {
        var obj = new JsonObject
        {
            ["model"] = request.ModelId,
            ["max_tokens"] = request.MaxTokens,
            ["stream"] = true,
            ["system"] = request.SystemPrompt
        };

        if (request.Temperature.HasValue)
            obj["temperature"] = request.Temperature.Value;

        var messages = new JsonArray();
        foreach (var msg in request.Messages)
        {
            var blocks = new JsonArray();
            IReadOnlyList<ContentBlock> content = msg switch
            {
                UserMessage u => u.Content,
                AssistantMessage a => a.Content,
                _ => []
            };

            foreach (var block in content)
            {
                JsonObject b = block switch
                {
                    TextBlock tb => new JsonObject { ["type"] = "text", ["text"] = tb.Text },
                    ThinkingBlock thk => new JsonObject { ["type"] = "thinking", ["thinking"] = thk.Thinking },
                    ToolUseBlock tu => new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = tu.Id,
                        ["name"] = tu.Name,
                        ["input"] = JsonNode.Parse(tu.Input.GetRawText())
                    },
                    ToolResultBlock tr => new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = tr.ToolUseId,
                        ["content"] = tr.Content,
                        ["is_error"] = tr.IsError
                    },
                    _ => new JsonObject { ["type"] = "text", ["text"] = "" }
                };
                blocks.Add(b);
            }

            messages.Add(new JsonObject { ["role"] = msg.Role, ["content"] = blocks });
        }
        obj["messages"] = messages;

        if (request.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var t in request.Tools)
            {
                tools.Add(new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["input_schema"] = JsonNode.Parse(t.InputSchema.GetRawText())
                });
            }
            obj["tools"] = tools;
        }

        return obj.ToJsonString();
    }

    private static async IAsyncEnumerable<ProviderEvent> ParseSseStream(
        HttpResponseMessage resp,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        // Track partial tool call state per block index
        var toolIds = new Dictionary<int, string>();
        var toolNames = new Dictionary<int, string>();
        var toolArgs = new Dictionary<int, StringBuilder>();
        int? currentBlockIndex = null;
        string? currentBlockType = null;

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
                break;
            if (!line.StartsWith("data: "))
                continue;

            var data = line[6..];
            if (data == "[DONE]")
                break;

            JsonElement ev;
            try { ev = JsonDocument.Parse(data).RootElement; }
            catch { continue; }

            var evType = ev.TryGetProperty("type", out var t) ? t.GetString() : null;

            switch (evType)
            {
                case "content_block_start":
                {
                    var idx = ev.TryGetProperty("index", out var i) ? i.GetInt32() : 0;
                    currentBlockIndex = idx;
                    if (!ev.TryGetProperty("content_block", out var cb))
                        break;
                    currentBlockType = cb.TryGetProperty("type", out var cbt) ? cbt.GetString() : null;

                    if (currentBlockType == "tool_use")
                    {
                        toolIds[idx] = cb.GetStringPropertyOrEmpty("id");
                        toolNames[idx] = cb.GetStringPropertyOrEmpty("name");
                        toolArgs[idx] = new StringBuilder();
                    }
                    break;
                }
                case "content_block_delta":
                {
                    var idx = ev.TryGetProperty("index", out var i) ? i.GetInt32() : currentBlockIndex ?? 0;
                    if (!ev.TryGetProperty("delta", out var delta))
                        break;

                    var deltaType = delta.TryGetProperty("type", out var dt) ? dt.GetString() : null;

                    if (deltaType == "text_delta")
                    {
                        var text = delta.GetStringPropertyOrEmpty("text");
                        yield return new TextDelta(text);
                    }
                    else if (deltaType == "thinking_delta")
                    {
                        var thinking = delta.GetStringPropertyOrEmpty("thinking");
                        yield return new ThinkingDelta(thinking);
                    }
                    else if (deltaType == "input_json_delta")
                    {
                        var partial = delta.GetStringPropertyOrEmpty("partial_json");
                        if (toolArgs.TryGetValue(idx, out var sb))
                            sb.Append(partial);
                    }
                    break;
                }
                case "content_block_stop":
                {
                    var idx = ev.TryGetProperty("index", out var i) ? i.GetInt32() : currentBlockIndex ?? 0;
                    if (toolArgs.TryGetValue(idx, out var argsSb))
                    {
                        var id = toolIds.GetValueOrDefault(idx, "");
                        var name = toolNames.GetValueOrDefault(idx, "");
                        yield return new ToolCallDelta(id, name, argsSb.ToString());
                        toolIds.Remove(idx);
                        toolNames.Remove(idx);
                        toolArgs.Remove(idx);
                    }
                    break;
                }
                case "message_delta":
                {
                    if (ev.TryGetProperty("usage", out var usage))
                    {
                        int input = usage.TryGetProperty("input_tokens", out var it) ? it.GetInt32() : 0;
                        int output = usage.TryGetProperty("output_tokens", out var ot) ? ot.GetInt32() : 0;
                        int cacheRead = usage.TryGetProperty("cache_read_input_tokens", out var cr) ? cr.GetInt32() : 0;
                        int cacheWrite = usage.TryGetProperty("cache_creation_input_tokens", out var cw) ? cw.GetInt32() : 0;
                        yield return new UsageUpdate(input, output, cacheRead, cacheWrite);
                    }

                    if (ev.TryGetProperty("delta", out var delta))
                    {
                        var stopReason = delta.TryGetProperty("stop_reason", out var sr) ? sr.GetString() : null;
                        var reason = stopReason switch
                        {
                            "end_turn" => StopReason.EndTurn,
                            "tool_use" => StopReason.ToolUse,
                            "max_tokens" => StopReason.MaxTokens,
                            "stop_sequence" => StopReason.StopSequence,
                            _ => StopReason.EndTurn
                        };
                        yield return new StreamEnd(reason);
                    }
                    break;
                }
                case "error":
                {
                    if (ev.TryGetProperty("error", out var errObj))
                    {
                        var msg = errObj.GetStringPropertyOrEmpty("message");
                        var errType = errObj.TryGetProperty("type", out var et) ? et.GetString() : null;
                        ProviderError provErr = errType switch
                        {
                            "authentication_error" => new AuthError(msg),
                            "rate_limit_error" => new RateLimitError(null),
                            "overloaded_error" => new ServerError(529),
                            _ when msg.ContainsNoCase("context") => new ContextLengthError(),
                            _ when msg.ContainsNoCase("model unknown") || 
                                   msg.ContainsNoCase("not found") && msg.ContainsNoCase("model") => new ModelUnknownError(msg),
                            _ => new RequestError(400, string.IsNullOrEmpty(msg) ? (errType ?? "") : msg)
                        };
                        yield return new StreamError(new ProviderException(provErr));
                    }
                    break;
                }
            }
        }
    }

    private static (string Type, string Message, string RequestId) ParseErrorBody(string body)
    {
        try
        {
            var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var reqId = root.GetStringPropertyOrEmpty("request_id");
            if (root.TryGetProperty("error", out var err))
            {
                var type = err.GetStringPropertyOrEmpty("type");
                var msg  = err.GetStringPropertyOrEmpty("message");
                return (type, msg, reqId);
            }
        }
        catch { }
        return ("", body, "");
    }

    private static async IAsyncEnumerable<ProviderEvent> HandleErrorResponse(
        HttpResponseMessage resp,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var body = await resp.Content.ReadAsStringAsync(ct);
        var status = (int)resp.StatusCode;

        ProviderError error;
        if (status == 401)
        {
            var (errType, errMsg, reqId) = ParseErrorBody(body);
            var detail = string.IsNullOrEmpty(errType) ? body : errType;
            error = new AuthError($"{detail}\nMessage: {errMsg}\nRequest ID: {reqId}".TrimEnd());
        }
        else if (ProviderHttp.TryClassifyCommonError(resp, out var commonError))
            error = commonError!;
        else if (body.ContainsNoCase("context") &&
                 body.ContainsNoCase("length"))
        {
            error = new ContextLengthError();
        }
        else
        {
            var (errType, errMsg, reqId) = ParseErrorBody(body);
            var detail = !string.IsNullOrEmpty(errMsg) ? errMsg
                       : !string.IsNullOrEmpty(errType) ? errType
                       : body.Trim();
            if (!string.IsNullOrEmpty(reqId))
                detail += $" (request {reqId})";
            if (detail.ContainsNoCase("model unknown") ||
                (detail.ContainsNoCase("model") && detail.ContainsNoCase("not found")))
            {
                error = new ModelUnknownError(detail);
            }
            else
            {
                error = new RequestError(status, detail);
            }
        }

        yield return new StreamError(new ProviderException(error));
    }
}
