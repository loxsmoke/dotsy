using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Dotsy.Core.Config;

namespace Dotsy.Core.Session;

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
