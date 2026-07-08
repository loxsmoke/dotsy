using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dotsy.Core.Config;
using Dotsy.Core.Tools;
using Dotsy.Core.Utils;

namespace Dotsy.Mcp;

public sealed class McpClient : IDisposable
{
    private readonly McpServerConfig serverConfig;
    private readonly HttpClient? httpClient;
    private Process? process;
    private StreamWriter? stdin;
    private StreamReader? stdout;
    private int requestId;

    public string ServerName => serverConfig.Name;
    public bool IsConnected { get; private set; }

    public McpClient(McpServerConfig config, HttpClient? http = null)
    {
        serverConfig = config;
        httpClient = http ?? new HttpClient();
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        if (serverConfig.Transport == McpTransport.Stdio)
        {
            if (string.IsNullOrEmpty(serverConfig.Command))
                throw new InvalidOperationException("stdio transport requires a command");

            process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = serverConfig.Command,
                    Arguments = string.Join(" ", serverConfig.Args),
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                }
            };
            process.Start();
            stdin = process.StandardInput;
            stdout = process.StandardOutput;

            // Send initialize
            await SendRequestAsync("initialize", new JsonObject
            {
                ["protocolVersion"] = "2024-11-05",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "dotsy", ["version"] = "1.0.0" }
            }, ct);
        }

        IsConnected = true;
    }

    public async Task<IReadOnlyList<McpToolDefinition>> ListToolsAsync(CancellationToken ct)
    {
        var result = await SendRequestAsync("tools/list", new JsonObject(), ct);
        var tools = new List<McpToolDefinition>();

        if (result?.TryGetProperty("tools", out var toolsEl) == true)
        {
            foreach (var tool in toolsEl.EnumerateArray())
            {
                var name = tool.GetStringPropertyOrEmpty("name");
                var desc = tool.GetStringPropertyOrEmpty("description");
                var schema = tool.TryGetProperty("inputSchema", out var s)
                    ? s.Clone()
                    : JsonDocument.Parse("{}").RootElement;

                tools.Add(new McpToolDefinition(name, desc, schema, serverConfig.Name));
            }
        }

        return tools;
    }

    public async Task<ToolResult> CallToolAsync(string toolName, JsonElement input, CancellationToken ct)
    {
        try
        {
            var result = await SendRequestAsync("tools/call", new JsonObject
            {
                ["name"] = toolName,
                ["arguments"] = JsonNode.Parse(input.GetRawText())
            }, ct);

            if (result is null)
                return ToolResult.Err("No response from MCP server");

            if (result.Value.TryGetProperty("isError", out var isErr) && isErr.GetBoolean())
            {
                var errContent = result.Value.TryGetProperty("content", out var ec)
                    ? GetContentText(ec) : "Unknown error";
                return ToolResult.Err(errContent);
            }

            var content = result.Value.TryGetProperty("content", out var c)
                ? GetContentText(c) : result.Value.GetRawText();

            return ToolResult.Ok(content);
        }
        catch (Exception ex)
        {
            return ToolResult.Err($"MCP call failed: {ex.Message}");
        }
    }

    private async Task<JsonElement?> SendRequestAsync(string method, JsonNode? params_, CancellationToken ct)
    {
        int id = Interlocked.Increment(ref requestId);

        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = params_
        };

        var json = request.ToJsonString();

        if (serverConfig.Transport == McpTransport.Http)
        {
            var resp = await httpClient!.PostAsync(
                serverConfig.Url,
                new StringContent(json, Encoding.UTF8, "application/json"),
                ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            return ParseResponse(body);
        }
        else
        {
            if (stdin is null || stdout is null)
                throw new InvalidOperationException("Not connected");

            await stdin.WriteLineAsync(json);
            await stdin.FlushAsync(ct);

            // An MCP stdio server may interleave JSON-RPC notifications (startup banners,
            // log messages, progress) with responses, and is not required to answer in
            // request order. Read until the line whose "id" matches this request; skip
            // notifications (which carry no "id"), unrelated responses, and non-JSON lines.
            while (true)
            {
                var line = await stdout.ReadLineAsync(ct);
                if (line is null)
                    return null;
                if (IsResponseForId(line, id))
                    return ParseResponse(line);
            }
        }
    }

    private static bool IsResponseForId(string json, int id)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            // Responses carry a matching "id"; notifications do not have one at all.
            return doc.RootElement.TryGetProperty("id", out var idEl)
                && idEl.ValueKind == JsonValueKind.Number
                && idEl.TryGetInt32(out var respId)
                && respId == id;
        }
        catch
        {
            return false; // stray non-JSON output on stdout — ignore it
        }
    }

    private static JsonElement? ParseResponse(string json)
    {
        var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("result", out var result))
            return result.Clone();
        return null;
    }

    private static string GetContentText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var item in content.EnumerateArray())
            {
                if (item.TryGetProperty("text", out var t))
                    sb.Append(t.GetString());
            }
            return sb.ToString();
        }
        return content.GetString() ?? content.GetRawText();
    }

    public void Dispose()
    {
        stdin?.Dispose();
        stdout?.Dispose();
        try { process?.Kill(); } catch { }
        process?.Dispose();
        IsConnected = false;
    }
}

public sealed record McpToolDefinition(
    string Name,
    string Description,
    System.Text.Json.JsonElement InputSchema,
    string ServerName);
