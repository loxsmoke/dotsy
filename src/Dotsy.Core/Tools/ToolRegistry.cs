using System.Collections.Concurrent;
using System.Text.Json;
using Dotsy.Core.Providers;
using Dotsy.Core.Skills;
using Dotsy.Core.Tools.Interfaces;

namespace Dotsy.Core.Tools;

public sealed class ToolRegistry
{
    private readonly ConcurrentDictionary<string, ITool> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string>? _log;

    public static readonly HashSet<string> BuiltInNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ReadTool.ToolName,
        WriteTool.ToolName,
        EditTool.ToolName,
        MultiEditTool.ToolName,
        ListTool.ToolName,
        GrepTool.ToolName,
        GlobTool.ToolName,
        FindDefsTool.ToolName,
        ShellTool.ToolName,
        WebFetchTool.ToolName,
        WebSearchTool.ToolName,
        SkillTool.ToolName,
        TodoTool.ToolName,
        AskTool.ToolName,
        DoneTool.ToolName,
        TaskTool.ToolName
    };

    public ToolRegistry(Action<string>? log = null)
    {
        _log = log;
    }

    public void Register(ITool tool)
    {
        _tools[tool.Name] = tool;
    }

    public bool TryGetTool(string name, out ITool? tool) =>
        _tools.TryGetValue(name, out tool);

    public bool Unregister(string name) =>
        _tools.TryRemove(name, out _);

    public IReadOnlyList<ToolDefinition> GetToolDefinitions() =>
        _tools.Values
            .Select(t => new ToolDefinition(t.Name, t.Description, t.InputSchema))
            .ToList();

    public IReadOnlyList<Interfaces.ITool> GetTools() =>
        _tools.Values.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public void RegisterMcpTool(ITool tool)
    {
        if (BuiltInNames.Contains(tool.Name))
            _log?.Invoke($"[warn] MCP tool '{tool.Name}' shadows a built-in tool with the same name");

        _tools[tool.Name] = tool;
    }

    public static ToolRegistry CreateWithBuiltIns(
        SkillDiscovery skillDiscovery,
        HttpClient? httpClient = null,
        Action<string>? log = null)
    {
        var registry = new ToolRegistry(log);
        registry.Register(new ReadTool());
        registry.Register(new WriteTool());
        registry.Register(new EditTool());
        registry.Register(new MultiEditTool());
        registry.Register(new ListTool());
        registry.Register(new GrepTool());
        registry.Register(new GlobTool());
        registry.Register(new FindDefsTool());
        registry.Register(new ShellTool());
        registry.Register(new WebFetchTool(httpClient));
        registry.Register(new WebSearchTool(httpClient));
        registry.Register(new SkillTool(skillDiscovery));
        registry.Register(new TodoTool());
        registry.Register(new AskTool());
        registry.Register(new DoneTool());
        registry.Register(new TaskTool());
        return registry;
    }
}
