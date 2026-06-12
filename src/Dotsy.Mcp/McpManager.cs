using Dotsy.Core.Config;
using Dotsy.Core.Tools;

namespace Dotsy.Mcp;

public sealed class McpManager : IDisposable
{
    private readonly List<McpClient> _clients = [];
    private readonly Dictionary<string, List<string>> _serverTools = new(StringComparer.OrdinalIgnoreCase);

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
                    if (!_serverTools.TryGetValue(serverConfig.Name, out var names))
                    {
                        names = [];
                        _serverTools[serverConfig.Name] = names;
                    }
                    names.Add(tool.Name);
                    log?.Invoke($"Registered MCP tool: {tool.Name}");
                }

                _clients.Add(client);
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
        var client = _clients.FirstOrDefault(c => c.ServerName == serverName);
        if (client is null) return;

        if (_serverTools.Remove(serverName, out var toolNames))
        {
            foreach (var toolName in toolNames)
                registry.Unregister(toolName);
        }

        _clients.Remove(client);
        client.Dispose();
    }

    public void Dispose()
    {
        foreach (var c in _clients)
            c.Dispose();
        _clients.Clear();
        _serverTools.Clear();
    }
}
