using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Dotsy.Core.Utils;
using Dotsy.Core.Tools.Interfaces;

namespace Dotsy.Core.Tools;

/// <summary>
/// Reads and updates the repository's <c>todo.md</c> task file. The model queries sections and
/// tasks (filtered by done/not-done) and flips individual checkboxes instead of reading and
/// re-editing the whole file, which keeps task tracking out of the conversation history.
/// </summary>
public sealed partial class TodoTool : ITool
{
    public const string ToolName = "Todo";
    public const string FileName = "todo.md";

    public string Name => ToolName;
    public string Description =>
        "Read and update the repository's todo.md task list. "
        + "Use list_sections to see sections, list_tasks to find work (filtered by section/status), "
        + "and set_status to mark a task done after you finish it.";
    public JsonElement InputSchema => ToolSchemas.TodoSchema;
    // ReadOnly so flipping a checkbox never blocks on an approval prompt; the write is confined to todo.md.
    public ToolSafety Safety => ToolSafety.ReadOnly;
    public bool IsCompletionSignal => false;

    public string FormatPanelArgument(JsonElement input, string cwd)
    {
        var action = input.GetStringPropertyOrEmpty("action");
        switch (action)
        {
            case "list_sections":
                return "List sections";
            case "list_tasks":
                var status = input.GetStringPropertyOrEmpty("status");
                if (string.IsNullOrEmpty(status)) status = "todo";
                return $"List tasks ({status})";
            case "set_status":
                var task = input.TryGetProperty("task", out var t) ? t.ToString() : "?";
                var state = input.TryGetProperty("done", out var d) && d.ValueKind == JsonValueKind.False ? "todo" : "done";
                return $"Set task {task} {state}";
            default:
                return "Todo";
        }
    }

    public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var action = input.GetStringPropertyOrEmpty("action");
        if (string.IsNullOrEmpty(action))
            return Task.FromResult(ToolResult.Err("action is required (list_sections, list_tasks, or set_status)"));

        var path = Path.Combine(ctx.Cwd, FileName);
        if (!File.Exists(path))
            return Task.FromResult(ToolResult.Err($"{FileName} not found in {ctx.Cwd}"));

        string raw;
        try { raw = File.ReadAllText(path); }
        catch (Exception ex) { return Task.FromResult(ToolResult.Err($"Could not read {FileName}: {ex.Message}")); }

        var lines = raw.Split('\n');
        var tasks = Parse(lines);

        return Task.FromResult(action switch
        {
            "list_sections" => ListSections(tasks),
            "list_tasks"    => ListTasks(tasks, input),
            "set_status"    => SetStatus(path, lines, tasks, input),
            _               => ToolResult.Err($"Unknown action '{action}'"),
        });
    }

    private sealed record TodoTask(int Index, string Section, bool Done, string Text, int LineNumber);

    private static List<TodoTask> Parse(string[] lines)
    {
        var tasks = new List<TodoTask>();
        var section = "(no section)";
        var index = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');

            var header = HeaderRegex().Match(line);
            if (header.Success)
            {
                section = header.Groups[1].Value.Trim();
                continue;
            }

            var task = TaskRegex().Match(line);
            if (task.Success)
            {
                var done = task.Groups[1].Value is "x" or "X";
                tasks.Add(new TodoTask(++index, section, done, task.Groups[2].Value.Trim(), i));
            }
        }

        return tasks;
    }

    private static ToolResult ListSections(List<TodoTask> tasks)
    {
        if (tasks.Count == 0)
            return ToolResult.Ok($"{FileName} has no tasks.");

        var sb = new StringBuilder();
        sb.AppendLine($"Sections in {FileName}:");
        foreach (var group in tasks.GroupBy(t => t.Section))
        {
            var done = group.Count(t => t.Done);
            var todo = group.Count(t => !t.Done);
            sb.AppendLine($"- {group.Key} ({todo} todo, {done} done)");
        }
        sb.Append("Use list_tasks with a section to see individual tasks.");
        return ToolResult.Ok(sb.ToString());
    }

    private static ToolResult ListTasks(List<TodoTask> tasks, JsonElement input)
    {
        var status = input.GetStringPropertyOrEmpty("status");
        if (string.IsNullOrEmpty(status)) status = "todo";

        var sectionFilter = input.GetStringPropertyOrEmpty("section");

        IEnumerable<TodoTask> filtered = tasks;
        if (!string.IsNullOrWhiteSpace(sectionFilter))
            filtered = filtered.Where(t => MatchesSection(t.Section, sectionFilter));
        filtered = status switch
        {
            "done" => filtered.Where(t => t.Done),
            "all"  => filtered,
            _      => filtered.Where(t => !t.Done),
        };

        var list = filtered.ToList();
        if (list.Count == 0)
            return ToolResult.Ok($"No {(status == "all" ? "" : status + " ")}tasks"
                + (string.IsNullOrWhiteSpace(sectionFilter) ? "" : $" in section '{sectionFilter}'") + ".");

        var sb = new StringBuilder();
        sb.AppendLine($"Tasks (status={status}). Reference a task by its [index] with set_status:");
        foreach (var t in list)
            sb.AppendLine($"[{t.Index}] {(t.Done ? "[x]" : "[ ]")} {t.Section}: {t.Text}");
        return ToolResult.Ok(sb.ToString().TrimEnd());
    }

    private static ToolResult SetStatus(string path, string[] lines, List<TodoTask> tasks, JsonElement input)
    {
        if (!input.TryGetProperty("task", out var taskEl) || !taskEl.TryGetInt32(out var taskIndex))
            return ToolResult.Err("set_status requires 'task' (the 1-based index from list_tasks)");

        var done = !input.TryGetProperty("done", out var doneEl) || doneEl.ValueKind != JsonValueKind.False;

        var target = tasks.FirstOrDefault(t => t.Index == taskIndex);
        if (target is null)
            return ToolResult.Err($"No task with index {taskIndex}. Call list_tasks to see valid indices.");

        if (target.Done == done)
            return ToolResult.Ok($"Task {taskIndex} was already {(done ? "done" : "todo")}: {target.Text}");

        var line = lines[target.LineNumber];
        var cr = line.EndsWith('\r');
        var core = cr ? line[..^1] : line;
        core = CheckboxRegex().Replace(core, done ? "[x]" : "[ ]", 1);
        lines[target.LineNumber] = cr ? core + "\r" : core;

        try { File.WriteAllText(path, string.Join('\n', lines)); }
        catch (Exception ex) { return ToolResult.Err($"Could not write {FileName}: {ex.Message}"); }

        return ToolResult.Ok($"Task {taskIndex} marked {(done ? "done" : "todo")}: {target.Text}");
    }

    private static bool MatchesSection(string section, string filter)
    {
        filter = filter.Trim();
        if (section.Contains(filter, StringComparison.OrdinalIgnoreCase))
            return true;
        // Match by leading number, e.g. filter "2" against "2. Bug fixes".
        var num = LeadingNumberRegex().Match(section);
        return num.Success && num.Groups[1].Value == filter;
    }

    [GeneratedRegex(@"^#{1,6}\s+(.+?)\s*$")]
    private static partial Regex HeaderRegex();

    [GeneratedRegex(@"^\s*[-*]\s+\[([ xX])\]\s*(.*)$")]
    private static partial Regex TaskRegex();

    [GeneratedRegex(@"\[[ xX]\]")]
    private static partial Regex CheckboxRegex();

    [GeneratedRegex(@"^\s*(\d+)\.")]
    private static partial Regex LeadingNumberRegex();
}
