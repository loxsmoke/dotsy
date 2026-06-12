using System.Text.Json;
using Dotsy.Core.Tools;

using Dotsy.Core.Tools.Interfaces;

namespace Dotsy.Mcp;

public sealed class McpTool : ITool
{
    private readonly McpClient _client;
    private readonly string _toolName;
    private readonly string _description;
    private readonly JsonElement _inputSchema;

    public string Name => $"[mcp:{_client.ServerName}]{_toolName}";
    public string Description => $"[{_client.ServerName}] {_description}";
    public JsonElement InputSchema => _inputSchema;
    public ToolSafety Safety => ToolSafety.Sequential;
    public bool IsCompletionSignal => false;

    public McpTool(McpClient client, McpToolDefinition def)
    {
        _client = client;
        _toolName = def.Name;
        _description = def.Description;
        _inputSchema = def.InputSchema;
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        if (!_client.IsConnected)
            return ToolResult.Err($"MCP server '{_client.ServerName}' is disconnected");

        return await _client.CallToolAsync(_toolName, input, ct);
    }
}
