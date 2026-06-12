using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Dotsy.Core.Utils;
using Dotsy.Core.Tools.Interfaces;

namespace Dotsy.Core.Tools;

public sealed class WebFetchTool : ITool
{
    private const int MaxBodyBytes = 100 * 1024;
    private const int ErrorBodyBytes = 5 * 1024 * 1024;

    private readonly HttpClient _http;

    public string Name => "WebFetch";
    public string Description => "Fetch a URL and return its content as Markdown. Upgrades HTTP to HTTPS.";
    public JsonElement InputSchema => ToolSchemas.WebFetchSchema;
    public ToolSafety Safety => ToolSafety.Sequential;
    public bool IsCompletionSignal => false;

    public string FormatRunApproval(JsonElement input, string cwd) =>
        input.GetStringPropertyOrEmpty("url");

    public string FormatPanelArgument(JsonElement input, string cwd) =>
        FormatRunApproval(input, cwd);

    public string? FormatPanelResult(JsonElement input, string resultContent, string cwd)
    {
        if (string.IsNullOrEmpty(resultContent)) return null;
        var url = input.GetStringPropertyOrEmpty("url");
        var bytes = Encoding.UTF8.GetByteCount(resultContent);
        var size = bytes >= 1024 ? $"{bytes / 1024} KB" : $"{bytes} B";
        return $"{url}  {size}";
    }

    public WebFetchTool(HttpClient? http = null)
    {
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });
        _http.DefaultRequestHeaders.Add("User-Agent", "dotsy/1.0");
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var url = input.GetProperty("url").GetString() ?? "";

        // Upgrade http to https
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url[7..];

        HttpResponseMessage resp;
        try
        {
            resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex)
        {
            return ToolResult.Err($"Fetch failed: {ex.Message}");
        }

        if (!resp.IsSuccessStatusCode)
            return ToolResult.Err($"HTTP {(int)resp.StatusCode}: {resp.ReasonPhrase}");

        var contentType = resp.Content.Headers.ContentType?.MediaType ?? "";
        var contentLength = resp.Content.Headers.ContentLength;

        if (contentLength > ErrorBodyBytes)
            return ToolResult.Err(
                $"Response is too large ({contentLength / 1024}KB). Use: curl -s {url} | head -c {MaxBodyBytes}");

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var buf = new byte[MaxBodyBytes + 1];
        int bytesRead = 0;
        int chunk;
        while ((chunk = await stream.ReadAsync(buf.AsMemory(bytesRead, Math.Min(1024, buf.Length - bytesRead)), ct)) > 0)
        {
            bytesRead += chunk;
            if (bytesRead > ErrorBodyBytes)
                return ToolResult.Err(
                    $"Response exceeded 5MB. Use: curl -s {url} | head -c {MaxBodyBytes}");
            if (bytesRead >= MaxBodyBytes)
                break;
        }

        var body = Encoding.UTF8.GetString(buf, 0, bytesRead);
        bool isHtml = contentType.Contains("html", StringComparison.OrdinalIgnoreCase)
                      || body.TrimStart().StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase)
                      || body.TrimStart().StartsWith("<html", StringComparison.OrdinalIgnoreCase);

        var result = isHtml ? HtmlToMarkdown(body) : body;

        if (bytesRead >= MaxBodyBytes)
            result += "\n\n<truncated: response exceeded 100KB>";

        return ToolResult.Ok(result);
    }

    private static string HtmlToMarkdown(string html)
    {
        // Strip script/style blocks
        html = Regex.Replace(html, @"<script[^>]*>[\s\S]*?</script>", "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<style[^>]*>[\s\S]*?</style>", "", RegexOptions.IgnoreCase);

        // Headings
        for (int i = 6; i >= 1; i--)
            html = Regex.Replace(html, $@"<h{i}[^>]*>([\s\S]*?)</h{i}>",
                m => new string('#', i) + " " + StripTags(m.Groups[1].Value) + "\n", RegexOptions.IgnoreCase);

        // Links
        html = Regex.Replace(html, @"<a[^>]+href=""([^""]+)""[^>]*>([\s\S]*?)</a>",
            m => $"[{StripTags(m.Groups[2].Value)}]({m.Groups[1].Value})", RegexOptions.IgnoreCase);

        // Bold/italic
        html = Regex.Replace(html, @"<(?:strong|b)[^>]*>([\s\S]*?)</(?:strong|b)>", "**$1**", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<(?:em|i)[^>]*>([\s\S]*?)</(?:em|i)>", "*$1*", RegexOptions.IgnoreCase);

        // Lists
        html = Regex.Replace(html, @"<li[^>]*>([\s\S]*?)</li>", m => "- " + StripTags(m.Groups[1].Value).Trim() + "\n", RegexOptions.IgnoreCase);

        // Paragraphs and line breaks
        html = Regex.Replace(html, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<p[^>]*>", "\n", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"</p>", "\n", RegexOptions.IgnoreCase);

        // Strip remaining tags
        html = StripTags(html);

        // Collapse whitespace
        html = Regex.Replace(html, @"\n{3,}", "\n\n");
        html = Regex.Replace(html, @" {2,}", " ");

        // Decode common HTML entities
        html = html.Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">")
                   .Replace("&quot;", "\"").Replace("&#39;", "'").Replace("&nbsp;", " ");

        return html.Trim();
    }

    private static string StripTags(string html) =>
        Regex.Replace(html, @"<[^>]+>", "");
}
