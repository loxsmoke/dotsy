using System.Text;
using System.Text.Json;
using Dotsy.Core.Config;
using Dotsy.Core.Tools;

namespace Dotsy.Core.Loop;

public sealed record SecuritySummaryRequest(
    DotsyConfig Config,
    PermissionStore Permissions,
    string Cwd,
    ToolRegistry? Registry = null,
    LoopContext? LoopContext = null,
    bool Headless = false);

public sealed class SecuritySummaryRenderer
{
    public string Render(SecuritySummaryRequest request)
    {
        var snapshot = request.Permissions.Snapshot();
        var sb = new StringBuilder();

        sb.AppendLine("Security summary");
        sb.AppendLine();
        AppendMode(sb, snapshot, request.Headless);
        AppendRuleSources(sb, snapshot);
        AppendPathAccess(sb, request, snapshot);
        AppendToolPermissions(sb, request, snapshot);
        AppendRules(sb, snapshot);
        AppendRecentDecisions(sb, snapshot);
        AppendNotes(sb);

        return sb.ToString()
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .TrimEnd();
    }

    private static void AppendMode(StringBuilder sb, PermissionStoreSnapshot snapshot, bool headless)
    {
        sb.AppendLine("Mode");
        sb.AppendLine($"  prompts: {(snapshot.Yolo ? "disabled" : headless ? "unavailable in headless mode" : "enabled")}");
        sb.AppendLine($"  yolo: {snapshot.Yolo.ToString().ToLowerInvariant()}");
        sb.AppendLine($"  headless: {headless.ToString().ToLowerInvariant()}");
        sb.AppendLine($"  project write approval: {(snapshot.AllowWriteForProject ? "enabled for project except .dotsy" : "not granted")}");
        sb.AppendLine();
    }

    private static void AppendRuleSources(StringBuilder sb, PermissionStoreSnapshot snapshot)
    {
        sb.AppendLine("Rule sources");
        sb.AppendLine($"  configured always allow: {CountOrNone(snapshot.ConfigAllow)}");
        sb.AppendLine($"  configured never allow: {CountOrNone(snapshot.ConfigDeny)}");
        sb.AppendLine($"  global permissions file: {snapshot.GlobalPermissionsPath}");
        sb.AppendLine($"  project permissions file: {snapshot.ProjectPermissionsPath}");
        sb.AppendLine($"  session/global/project allow entries loaded: {CountOrNone(snapshot.SessionAllow)}");
        sb.AppendLine($"  session/global/project deny entries loaded: {CountOrNone(snapshot.SessionDeny)}");
        sb.AppendLine();
    }

    private static void AppendPathAccess(StringBuilder sb, SecuritySummaryRequest request, PermissionStoreSnapshot snapshot)
    {
        var cwd = Path.GetFullPath(request.Cwd);
        var normalWrite = JsonPath(Path.Combine(cwd, "sample.txt"));
        var dotsyWrite = JsonPath(Path.Combine(cwd, ".dotsy", "sample.txt"));
        var outsideWrite = JsonPath(Path.Combine(Path.GetTempPath(), "dotsy-outside-sec-sample.txt"));

        sb.AppendLine("Path access");
        sb.AppendLine($"  {cwd}  write tools: {VerdictLabel(request.Permissions.Evaluate("Write", normalWrite))}");
        sb.AppendLine($"  {Path.Combine(cwd, ".dotsy")}  write tools: {VerdictLabel(request.Permissions.Evaluate("Write", dotsyWrite))}");

        foreach (var file in request.LoopContext?.AddedFiles ?? [])
            sb.AppendLine($"  /add {file}  read-only context; write tools: {VerdictLabel(request.Permissions.Evaluate("Write", JsonPath(file)))}");

        if ((request.LoopContext?.AddedFiles.Count ?? 0) == 0)
            sb.AppendLine("  /add files  none added");

        sb.AppendLine($"  outside {cwd}  write tools: {VerdictLabel(request.Permissions.Evaluate("Write", outsideWrite))}");
        sb.AppendLine();
    }

    private static void AppendToolPermissions(StringBuilder sb, SecuritySummaryRequest request, PermissionStoreSnapshot snapshot)
    {
        var tools = request.Registry?.GetTools() ?? [];
        sb.AppendLine("Tool permissions");
        if (tools.Count == 0)
        {
            sb.AppendLine("  registered tools: not yet detailed");
            sb.AppendLine();
            return;
        }

        foreach (var group in tools.GroupBy(ToolGroup).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var names = string.Join(", ", group.Select(t => t.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
            var behavior = group.Key switch
            {
                "read-only" => "allow",
                "write" when snapshot.Yolo => "allow",
                "write" => "ask in cwd; no access outside cwd; .dotsy asks even with project approval",
                "shell" when snapshot.Yolo => "allow",
                "shell" => "ask unless allowed or denied by rule",
                "task subagent" when snapshot.Yolo => "allow",
                "task subagent" => "ask",
                "skills" => "allow as read-only tool; skill-specific permission details not yet detailed",
                "mcp" => "ask or allow depends on exposed tool safety; external server trust not yet detailed",
                _ when snapshot.Yolo => "allow",
                _ => "ask"
            };
            sb.AppendLine($"  {names}  {behavior}");
        }
        sb.AppendLine();
    }

    private static void AppendRules(StringBuilder sb, PermissionStoreSnapshot snapshot)
    {
        sb.AppendLine("Effective rules");
        foreach (var rule in snapshot.HardDenials)
            sb.AppendLine($"  deny  {SummarizeRule(rule)}");
        sb.AppendLine("  deny  Write, Edit, and MultiEdit outside the current working directory");
        AppendRuleList(sb, "deny ", snapshot.ConfigDeny);
        AppendRuleList(sb, "deny ", snapshot.SessionDeny);
        AppendRuleList(sb, "allow", snapshot.ConfigAllow);
        AppendRuleList(sb, "allow", snapshot.SessionAllow);
        if (snapshot.AllowWriteForProject)
            sb.AppendLine("  allow Write, Edit, and MultiEdit inside the project except .dotsy");
        sb.AppendLine("  ask   non-read-only tools without a matching allow or deny");
        sb.AppendLine();
    }

    private static void AppendRecentDecisions(StringBuilder sb, PermissionStoreSnapshot snapshot)
    {
        sb.AppendLine("Recent decisions");
        if (snapshot.RecentDecisions.Count == 0)
        {
            sb.AppendLine("  none recorded for this process");
            sb.AppendLine();
            return;
        }

        foreach (var decision in snapshot.RecentDecisions)
            sb.AppendLine($"  {decision.Kind}: {SummarizeRule(decision.Rule)}");
        sb.AppendLine();
    }

    private static void AppendNotes(StringBuilder sb)
    {
        sb.AppendLine("Notes");
        sb.AppendLine("  /sec is display-only and does not mutate permission state.");
        sb.AppendLine("  MCP server trust, skill body approval history, and nested tool prompts are in progress where the current permission model does not expose exact detail.");
    }

    private static string ToolGroup(Dotsy.Core.Tools.Interfaces.ITool tool)
    {
        if (tool.IsWriteTool || tool.Name is WriteTool.ToolName or  EditTool.ToolName or MultiEditTool.ToolName)
            return "write";
        if (tool.Name.Equals(ShellTool.ToolName, StringComparison.OrdinalIgnoreCase))
            return "shell";
        if (tool.Name.Equals(TaskTool.ToolName, StringComparison.OrdinalIgnoreCase))
            return "task subagent";
        if (tool.Name.Equals(SkillTool.ToolName, StringComparison.OrdinalIgnoreCase))
            return "skills";
        if (!ToolRegistry.BuiltInNames.Contains(tool.Name))
            return "mcp";
        if (tool.Safety == ToolSafety.ReadOnly)
            return "read-only";
        return tool.Safety.ToString().ToLowerInvariant();
    }


    private static string CountOrNone(IReadOnlyCollection<string> items) =>
        items.Count == 0 ? "none" : items.Count.ToString();

    private static void AppendRuleList(StringBuilder sb, string label, IReadOnlyList<string> rules)
    {
        const int maxRules = 20;
        foreach (var rule in rules.Take(maxRules))
            sb.AppendLine($"  {label} {SummarizeRule(rule)}");
        if (rules.Count > maxRules)
            sb.AppendLine($"  {label} ... {rules.Count - maxRules} more rules omitted");
    }

    private static string SummarizeRule(string rule)
    {
        var open = rule.IndexOf('(');
        var close = rule.LastIndexOf(')');
        if (open <= 0 || close <= open)
            return ClipSingleLine(rule, 180);

        var toolName = rule[..open];
        var argument = rule[(open + 1)..close];
        return $"{toolName} {SummarizeArgument(toolName, argument)}";
    }

    private static string SummarizeArgument(string toolName, string argument)
    {
        var trimmed = argument.Trim();
        if (!trimmed.StartsWith('{'))
        {
            return SummarizePlainArgument(trimmed);
        }

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;

            if (toolName.Equals(WriteTool.ToolName, StringComparison.OrdinalIgnoreCase) ||
                toolName.Equals(EditTool.ToolName, StringComparison.OrdinalIgnoreCase) ||
                toolName.Equals(MultiEditTool.ToolName, StringComparison.OrdinalIgnoreCase) ||
                toolName.Equals(ReadTool.ToolName, StringComparison.OrdinalIgnoreCase))
            {
                return SummarizeJsonProperty(root, "path", "path");
            }

            if (toolName.Equals(ShellTool.ToolName, StringComparison.OrdinalIgnoreCase))
            {
                return SummarizeJsonProperty(root, "command", "command");
            }

            if (toolName.Equals(GrepTool.ToolName, StringComparison.OrdinalIgnoreCase) ||
                toolName.Equals(WebSearchTool.ToolName, StringComparison.OrdinalIgnoreCase))
            {
                return SummarizeJsonProperty(root, "query", "query");
            }

            if (toolName.Equals(GlobTool.ToolName, StringComparison.OrdinalIgnoreCase))
            {
                return SummarizeJsonProperty(root, "pattern", "pattern");
            }

            if (toolName.Equals(WebFetchTool.ToolName, StringComparison.OrdinalIgnoreCase))
            {
                return SummarizeJsonProperty(root, "url", "url");
            }

            if (toolName.Equals(SkillTool.ToolName, StringComparison.OrdinalIgnoreCase))
            {
                return SummarizeJsonProperty(root, "name", "name");
            }

            if (toolName.Equals(TaskTool.ToolName, StringComparison.OrdinalIgnoreCase))
            {
                return root.TryGetProperty("task_id", out var taskId)
                    ? $"task id {ClipSingleLine(taskId.GetString() ?? "", 80)}"
                    : SummarizeJsonProperty(root, "description", "description");
            }

            return SummarizeJsonObject(root);
        }
        catch
        {
            return SummarizePlainArgument(trimmed);
        }
    }

    private static string SummarizeJsonProperty(JsonElement root, string propertyName, string label)
    {
        if (root.TryGetProperty(propertyName, out var value))
            return $"{label} {ClipSingleLine(value.GetString() ?? value.GetRawText(), 140)}";
        return SummarizeJsonObject(root);
    }

    private static string SummarizeJsonObject(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return ClipSingleLine(root.GetRawText(), 160);

        var names = root.EnumerateObject().Select(p => p.Name).Take(8).ToArray();
        var suffix = root.EnumerateObject().Skip(8).Any() ? ", ..." : "";
        return $"json fields {string.Join(", ", names)}{suffix}";
    }

    private static string SummarizePlainArgument(string value)
    {
        var plain = value
            .Replace("(", " ", StringComparison.Ordinal)
            .Replace(")", " ", StringComparison.Ordinal)
            .Replace("{", " ", StringComparison.Ordinal)
            .Replace("}", " ", StringComparison.Ordinal)
            .Replace("\"", "", StringComparison.Ordinal)
            .Replace("*", "any", StringComparison.Ordinal);
        return ClipSingleLine(plain, 160);
    }

    private static string ClipSingleLine(string value, int max)
    {
        var normalized = value
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", " ", StringComparison.Ordinal);

        normalized = ToDisplayAscii(normalized);

        if (normalized.Length <= max)
            return normalized;
        return normalized[..Math.Max(0, max - 3)] + "...";
    }

    private static string ToDisplayAscii(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch is >= ' ' and <= '~')
                sb.Append(ch);
            else
                sb.Append('?');
        }
        return sb.ToString();
    }

    private static string VerdictLabel(PermissionVerdict verdict) => verdict switch
    {
        PermissionVerdict.Allow => "allow",
        PermissionVerdict.Ask => "ask",
        PermissionVerdict.Deny => "no access",
        _ => "not yet detailed"
    };

    private static string JsonPath(string path) =>
        "{\"path\":\"" + path.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"}";
}
