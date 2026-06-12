using System.Collections.Concurrent;
using System.Text.Json;
using Dotsy.Core.Providers;

using Dotsy.Core.Tools.Interfaces;

namespace Dotsy.Core.Tools;

public sealed class ToolRegistry
{
    private readonly ConcurrentDictionary<string, ITool> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string>? _log;

    private static readonly HashSet<string> BuiltInNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Read", "Write", "Edit", "MultiEdit", "List",
        "Grep", "Glob", "FindDefinitions",
        "Shell", "WebFetch", "WebSearch",
        "Skill", "Todo", "Ask", "Done", "Task"
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
        Loop.SkillDiscovery skillDiscovery,
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
        registry.Register(new FindDefinitionsTool());
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
