using Dotsy.Core.Config;
using Dotsy.Core.Tools;

namespace Dotsy.Mcp;

public sealed class McpManager : IDisposable
{
    private readonly List<McpClient> mcpClients = [];
    private readonly Dictionary<string, List<string>> serverTools = new(StringComparer.OrdinalIgnoreCase);

    public async Task ConnectAllAsync(
        IEnumerable<McpServerConfig> servers,
        ToolRegistry registry,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        foreach (var serverConfig in servers)
        {
            var client = new McpClient(serverConfig);
            try
            {
                await client.ConnectAsync(ct);
                var tools = await client.ListToolsAsync(ct);

                foreach (var def in tools)
                {
                    var tool = new McpTool(client, def);
                    registry.RegisterMcpTool(tool);
                    if (!serverTools.TryGetValue(serverConfig.Name, out var names))
                    {
                        names = [];
                        serverTools[serverConfig.Name] = names;
                    }
                    names.Add(tool.Name);
                    log?.Invoke($"Registered MCP tool: {tool.Name}");
                }

                mcpClients.Add(client);
            }
            catch (Exception ex)
            {
                log?.Invoke($"[warn] Failed to connect MCP server '{serverConfig.Name}': {ex.Message}");
                client.Dispose();
            }
        }
    }

    public void DisconnectServer(string serverName, ToolRegistry registry)
    {
        var client = mcpClients.FirstOrDefault(c => c.ServerName == serverName);
        if (client is null) return;

        if (serverTools.Remove(serverName, out var toolNames))
        {
            foreach (var toolName in toolNames)
                registry.Unregister(toolName);
        }

        mcpClients.Remove(client);
        client.Dispose();
    }

    public void Dispose()
    {
        foreach (var c in mcpClients)
            c.Dispose();
        mcpClients.Clear();
        serverTools.Clear();
    }
}
