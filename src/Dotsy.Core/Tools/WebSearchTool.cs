using System.Text.Json;
using System.Web;

using Dotsy.Core.Utils;
using Dotsy.Core.Tools.Interfaces;

namespace Dotsy.Core.Tools;

public sealed class WebSearchTool : ITool
{
    private readonly HttpClient _http;

    public string Name => "WebSearch";
    public string Description => "Search the web and return titles, snippets, and URLs.";
    public JsonElement InputSchema => ToolSchemas.WebSearchSchema;
    public ToolSafety Safety => ToolSafety.Sequential;
    public bool IsCompletionSignal => false;

    public string FormatRunApproval(JsonElement input, string cwd)
    {
        var query = input.GetStringPropertyOrEmpty("query");
        return $"\"{query}\"";
    }

    public string FormatPanelArgument(JsonElement input, string cwd) =>
        FormatRunApproval(input, cwd);

    public string? FormatPanelResult(JsonElement input, string resultContent, string cwd)
    {
        if (string.IsNullOrEmpty(resultContent)) return null;
        var query = input.GetStringPropertyOrEmpty("query");
        var results = resultContent.Split('\n').Count(l => l.StartsWith("- "));
        return $"\"{query}\"  {results} results";
    }

    public WebSearchTool(HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
        _http.DefaultRequestHeaders.Add("User-Agent", "dotsy/1.0");
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var query = input.GetProperty("query").GetString() ?? "";

        // Use DuckDuckGo Instant Answer API
        var encoded = HttpUtility.UrlEncode(query);
        var url = $"https://api.duckduckgo.com/?q={encoded}&format=json&no_html=1&skip_disambig=1";

        HttpResponseMessage resp;
        try
        {
            resp = await _http.GetAsync(url, ct);
        }
        catch (Exception ex)
        {
            return ToolResult.Err($"Search failed: {ex.Message}");
        }

        if (!resp.IsSuccessStatusCode)
            return ToolResult.Err($"Search API returned HTTP {(int)resp.StatusCode}");

        var json = await resp.Content.ReadAsStringAsync(ct);
        var doc = JsonDocument.Parse(json).RootElement;

        var sb = new System.Text.StringBuilder();

        // Abstract / direct answer
        if (doc.TryGetProperty("AbstractText", out var abstract_) && abstract_.GetString() is { Length: > 0 } abstractText)
        {
            var abstractUrl = doc.GetStringPropertyOrEmpty("AbstractURL");
            sb.AppendLine($"**{abstractText}**");
            if (!string.IsNullOrEmpty(abstractUrl))
                sb.AppendLine(abstractUrl);
            sb.AppendLine();
        }

        // Related topics
        if (doc.TryGetProperty("RelatedTopics", out var topics))
        {
            int count = 0;
            foreach (var topic in topics.EnumerateArray())
            {
                if (count >= 10) break;
                var text = topic.GetStringPropertyOrEmpty("Text");
                var link = topic.GetStringPropertyOrEmpty("FirstURL");
                if (string.IsNullOrEmpty(text) && string.IsNullOrEmpty(link)) continue;

                sb.AppendLine($"- {text}");
                if (!string.IsNullOrEmpty(link))
                    sb.AppendLine($"  {link}");
                count++;
            }
        }

        if (sb.Length == 0)
            return ToolResult.Ok($"No results found for: {query}");

        return ToolResult.Ok(sb.ToString().TrimEnd());
    }
}
