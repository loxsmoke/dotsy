using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Dotsy.Core.Config;
using Dotsy.Core.Loop;
using Dotsy.Core.Providers;

namespace Dotsy.Core.Session;

public sealed class TrajectoryDocument
{
    [JsonPropertyName("question_category")]
    public string QuestionCategory { get; set; } = "unknown";

    [JsonPropertyName("complexity_level")]
    public string ComplexityLevel { get; set; } = "unknown";

    [JsonPropertyName("question")]
    public string Question { get; set; } = "";

    [JsonPropertyName("agent_prompt")]
    public string AgentPrompt { get; set; } = "";

    [JsonPropertyName("enabled_tools")]
    public List<string> EnabledTools { get; set; } = [];

    [JsonPropertyName("skills_path")]
    public string SkillsPath { get; set; } = "";

    [JsonPropertyName("uuid")]
    public string Uuid { get; set; } = "";

    [JsonPropertyName("messages")]
    public List<JsonObject> Messages { get; set; } = [];

    [JsonPropertyName("tools")]
    public List<TrajectoryToolRow> Tools { get; set; } = [];

    [JsonPropertyName("metadata")]
    public TrajectoryMetadata Metadata { get; set; } = new();

    [JsonPropertyName("hf_split")]
    public string HfSplit { get; set; } = "dotsy";
}

public sealed class TrajectoryToolRow
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("inputSchema")]
    public TrajectoryInputSchema InputSchema { get; set; } = new();
}

public sealed class TrajectoryInputSchema
{
    [JsonPropertyName("jsonSchema")]
    public JsonElement JsonSchema { get; set; }
}

public sealed class TrajectoryMetadata
{
    [JsonPropertyName("uuid")]
    public string Uuid { get; set; } = "";

    [JsonPropertyName("dotsy_version")]
    public string DotsyVersion { get; set; } = "1.0.0";

    [JsonPropertyName("cwd")]
    public string Cwd { get; set; } = "";

    [JsonPropertyName("git_branch")]
    public string? GitBranch { get; set; }

    [JsonPropertyName("git_commit")]
    public string? GitCommit { get; set; }

    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "";

    [JsonPropertyName("started_at")]
    public DateTimeOffset StartedAt { get; set; }

    [JsonPropertyName("ended_at")]
    public DateTimeOffset EndedAt { get; set; }

    [JsonPropertyName("duration_ms")]
    public long DurationMs { get; set; }

    [JsonPropertyName("token_usage")]
    public TrajectoryTokenUsage TokenUsage { get; set; } = new();

    [JsonPropertyName("outcome")]
    public string Outcome { get; set; } = "";

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public sealed class TrajectoryTokenUsage
{
    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; set; }

    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; set; }

    [JsonPropertyName("cache_read_tokens")]
    public int CacheReadTokens { get; set; }

    [JsonPropertyName("cache_write_tokens")]
    public int CacheWriteTokens { get; set; }
}

public sealed class TrajectoryRecorder
{
    private readonly DotsyConfig _config;
    private readonly string _cwd;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private ChatRequest? _initialRequest;

    public TrajectoryTokenUsage TokenUsage { get; } = new();

    public TrajectoryRecorder(DotsyConfig config, string cwd)
    {
        _config = config;
        _cwd = cwd;
    }

    public void CaptureInitialRequest(ChatRequest request)
    {
        _initialRequest ??= request;
    }

    public void RecordUsage(int inputTokens, int outputTokens, int cacheReadTokens, int cacheWriteTokens)
    {
        TokenUsage.InputTokens += inputTokens;
        TokenUsage.OutputTokens += outputTokens;
        TokenUsage.CacheReadTokens += cacheReadTokens;
        TokenUsage.CacheWriteTokens += cacheWriteTokens;
    }

    public void Export(LoopContext ctx, EndReason reason, string? error = null)
    {
        if (!_config.Trajectory.Enabled || _initialRequest is null)
            return;

        var endedAt = DateTimeOffset.UtcNow;
        var doc = new TrajectoryDocument
        {
            Question = FirstUserQuestion(ctx),
            AgentPrompt = _initialRequest.SystemPrompt,
            EnabledTools = [.. _initialRequest.Tools.Select(t => t.Name)],
            SkillsPath = string.Join(Path.PathSeparator, _config.Skills.Paths),
            Uuid = ctx.SessionId,
            Messages = TrajectoryConverter.ToOpenAiMessages(_initialRequest.SystemPrompt, ctx),
            Tools = TrajectoryConverter.ToToolRows(_initialRequest.Tools),
            Metadata = new TrajectoryMetadata
            {
                Uuid = ctx.SessionId,
                Cwd = _cwd,
                GitBranch = TryGetGitBranch(_cwd),
                GitCommit = TryGetGitCommit(_cwd),
                Model = _config.Model.ActiveModelId,
                Provider = _config.Model.Provider,
                StartedAt = _startedAt,
                EndedAt = endedAt,
                DurationMs = (long)(endedAt - _startedAt).TotalMilliseconds,
                TokenUsage = TokenUsage,
                Outcome = Outcome(reason),
                Error = error
            }
        };

        var redacted = TrajectoryRedactor.Redact(doc, _config);
        var dir = ResolveDir(_config.Trajectory.Dir, _cwd);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{ctx.SessionId}.json");
        File.WriteAllText(path, redacted, System.Text.Encoding.UTF8);
    }

    private static string ResolveDir(string dir, string cwd) =>
        Path.IsPathRooted(dir) ? dir : Path.Combine(cwd, dir);

    private static string FirstUserQuestion(LoopContext ctx) =>
        ctx.Messages
            .OfType<UserMessage>()
            .SelectMany(m => m.Content)
            .OfType<TextBlock>()
            .Select(b => b.Text)
            .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? "";

    private static string Outcome(EndReason reason) => reason switch
    {
        EndReason.TaskComplete => "completed",
        EndReason.TurnLimitReached => "turn_limit",
        EndReason.NudgeLimitReached => "nudge_limit",
        EndReason.ContextTooSmall => "context_too_small",
        EndReason.Cancelled => "cancelled",
        EndReason.Error => "error",
        _ => reason.ToString()
    };

    private static string? TryGetGitBranch(string cwd)
    {
        try
        {
            var path = LibGit2Sharp.Repository.Discover(cwd);
            if (path is null) return null;
            using var repo = new LibGit2Sharp.Repository(path);
            return repo.Head.FriendlyName;
        }
        catch { return null; }
    }

    private static string? TryGetGitCommit(string cwd)
    {
        try
        {
            var path = LibGit2Sharp.Repository.Discover(cwd);
            if (path is null) return null;
            using var repo = new LibGit2Sharp.Repository(path);
            return repo.Head.Tip?.Sha;
        }
        catch { return null; }
    }
}

public static class TrajectoryConverter
{
    public static List<TrajectoryToolRow> ToToolRows(IReadOnlyList<ToolDefinition> tools) =>
        [.. tools.Select(t => new TrajectoryToolRow
        {
            Id = t.Name,
            Description = t.Description,
            InputSchema = new TrajectoryInputSchema { JsonSchema = t.InputSchema.Clone() }
        })];

    public static List<JsonObject> ToOpenAiMessages(string systemPrompt, LoopContext ctx)
    {
        var messages = new List<JsonObject> { new() { ["role"] = "system", ["content"] = systemPrompt } };

        if (!string.IsNullOrWhiteSpace(ctx.CompactionSummary))
        {
            messages.Add(new JsonObject
            {
                ["role"] = "system",
                ["content"] = $"Provider-facing compaction summary:\n{ctx.CompactionSummary}"
            });
        }

        foreach (var msg in ctx.Messages)
        {
            switch (msg)
            {
                case UserMessage user:
                    AddUserMessages(messages, user);
                    break;
                case AssistantMessage assistant:
                    messages.Add(ToAssistantMessage(assistant));
                    break;
            }
        }

        return messages;
    }

    private static void AddUserMessages(List<JsonObject> messages, UserMessage user)
    {
        var textParts = new List<string>();
        foreach (var block in user.Content)
        {
            if (block is ToolResultBlock tr)
            {
                if (textParts.Count > 0)
                {
                    messages.Add(new JsonObject { ["role"] = "user", ["content"] = string.Join("\n", textParts) });
                    textParts.Clear();
                }

                messages.Add(new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = tr.ToolUseId,
                    ["name"] = "",
                    ["content"] = tr.Content
                });
            }
            else if (block is TextBlock tb)
            {
                textParts.Add(tb.Text);
            }
        }

        if (textParts.Count > 0)
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = string.Join("\n", textParts) });
    }

    private static JsonObject ToAssistantMessage(AssistantMessage assistant)
    {
        var content = string.Join("\n", assistant.Content.OfType<TextBlock>().Select(b => b.Text));
        var obj = new JsonObject { ["role"] = "assistant", ["content"] = content };
        var toolCalls = new JsonArray();

        foreach (var toolUse in assistant.Content.OfType<ToolUseBlock>())
        {
            toolCalls.Add(new JsonObject
            {
                ["id"] = toolUse.Id,
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = toolUse.Name,
                    ["arguments"] = toolUse.Input.GetRawText()
                }
            });
        }

        if (toolCalls.Count > 0)
            obj["tool_calls"] = toolCalls;

        return obj;
    }
}

public static partial class TrajectoryRedactor
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    public static string Redact<T>(T value, DotsyConfig config)
    {
        var secrets = CollectSecrets(config);
        var node = JsonSerializer.SerializeToNode(value, JsonOpts) ?? new JsonObject();
        RedactNode(node, secrets);
        return node.ToJsonString(JsonOpts);
    }

    private static void RedactNode(JsonNode? node, IReadOnlyList<string> secrets)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    if (obj[key] is JsonValue val && val.TryGetValue<string>(out var s))
                        obj[key] = RedactString(s, secrets);
                    else
                        RedactNode(obj[key], secrets);
                }
                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    if (arr[i] is JsonValue val && val.TryGetValue<string>(out var s))
                        arr[i] = RedactString(s, secrets);
                    else
                        RedactNode(arr[i], secrets);
                }
                break;
        }
    }

    private static string RedactString(string text, IReadOnlyList<string> secrets)
    {
        var redacted = text;
        foreach (var secret in secrets)
            redacted = redacted.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        redacted = BearerRegex().Replace(redacted, "Bearer [REDACTED]");
        redacted = ApiKeyRegex().Replace(redacted, "[REDACTED]");
        return redacted;
    }

    private static List<string> CollectSecrets(DotsyConfig config)
    {
        var secrets = new List<string>
        {
            config.Model.Anthropic.ApiKey,
            config.Model.OpenAi.ApiKey,
            config.Model.Azure.ApiKey,
            config.Model.Compatible.ApiKey,
            config.Model.Gemini.ApiKey
        };

        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = entry.Key?.ToString() ?? "";
            var value = entry.Value?.ToString() ?? "";
            if (value.Length < 8)
                continue;
            if (key.Contains("KEY", StringComparison.OrdinalIgnoreCase)
                || key.Contains("TOKEN", StringComparison.OrdinalIgnoreCase)
                || key.Contains("SECRET", StringComparison.OrdinalIgnoreCase)
                || key.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase)
                || key.Contains("AUTH", StringComparison.OrdinalIgnoreCase))
            {
                secrets.Add(value);
            }
        }

        return [.. secrets.Where(s => !string.IsNullOrWhiteSpace(s) && s.Length >= 8).Distinct()];
    }

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9._~+/=-]+", RegexOptions.IgnoreCase)]
    private static partial Regex BearerRegex();

    [GeneratedRegex(@"(?:sk|ak|pk|rk|xox[baprs])-[A-Za-z0-9_\-]{16,}")]
    private static partial Regex ApiKeyRegex();
}
