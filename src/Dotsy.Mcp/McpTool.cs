using System.Text.Json;
using Dotsy.Core.Tools;

using Dotsy.Core.Tools.Interfaces;

namespace Dotsy.Mcp;

public sealed class McpTool : ITool
{
    private readonly McpClient mcpClient;
    private readonly string toolName;
    private readonly string description;
    private readonly JsonElement inputSchema;

    public string Name => $"[mcp:{mcpClient.ServerName}]{toolName}";
    public string Description => $"[{mcpClient.ServerName}] {description}";
    public JsonElement InputSchema => inputSchema;
    public ToolSafety Safety => ToolSafety.Sequential;
    public bool IsCompletionSignal => false;

    public McpTool(McpClient client, McpToolDefinition def)
    {
        mcpClient = client;
        toolName = def.Name;
        description = def.Description;
        inputSchema = def.InputSchema;
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        if (!mcpClient.IsConnected)
            return ToolResult.Err($"MCP server '{mcpClient.ServerName}' is disconnected");

        return await mcpClient.CallToolAsync(toolName, input, ct);
    }
}
