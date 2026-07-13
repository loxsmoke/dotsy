using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Dotsy.Core.Utils;
using Dotsy.Core.Tools.Interfaces;

namespace Dotsy.Core.Tools;

/// <summary>
/// Reads and updates the repository's <c>todo.md</c> task file. The model queries sections and
/// tasks (filtered by done/not-done) and edits sections/tasks through structured actions instead
/// of reading and re-editing the whole file, which keeps task tracking out of the conversation
/// history.
/// </summary>
public sealed partial class TodoTool : ITool
{
    public const string ToolName = "Todo";
    public const string FileName = "todo.md";

    public string Name => ToolName;
    public string Description =>
        "Read and update the repository's todo.md task list. "
        + "Actions: list (default) shows sections and tasks with their indexes; "
        + "add appends a task (creates the section if needed); "
        + "update rewrites (text), completes (done), or deletes (delete: true) a task by index; "
        + "section creates, renames, or deletes a section.";
    public JsonElement InputSchema => ToolSchemas.TodoSchema;
    // ReadOnly so structured todo.md maintenance never blocks on an approval prompt; writes are confined to todo.md.
    public ToolSafety Safety => ToolSafety.ReadOnly;
    public bool IsCompletionSignal => false;

    public string FormatPanelArgument(JsonElement input, string cwd)
    {
        var action = NormalizeAction(input.GetStringPropertyOrEmpty("action"));
        switch (action)
        {
            case "" or "list":
                return "List tasks";
            case "add":
                return $"Add task{SectionSuffix(input)}";
            case "update":
                var updateTask = InputValue(input, "task");
                if (FlexBool(input, "delete") == true)
                    return $"Delete task {updateTask}";
                return $"Update task {updateTask}";
            case "section":
                if (FlexBool(input, "delete") == true)
                    return $"Delete section {input.GetStringPropertyOrEmpty("section")}";
                return $"Section {(string.IsNullOrEmpty(input.GetStringPropertyOrEmpty("title")) ? input.GetStringPropertyOrEmpty("section") : input.GetStringPropertyOrEmpty("title"))}";
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
            case "create_section":
                return $"Create section {input.GetStringPropertyOrEmpty("title")}";
            case "edit_section":
                return $"Edit section {input.GetStringPropertyOrEmpty("section")}";
            case "delete_section":
                return $"Delete section {input.GetStringPropertyOrEmpty("section")}";
            case "create_item":
                return $"Create item in {input.GetStringPropertyOrEmpty("section")}";
            case "edit_item":
                return $"Edit task {InputValue(input, "task")}";
            case "delete_item":
                return $"Delete task {InputValue(input, "task")}";
            default:
                return "Todo";
        }
    }

    public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var rawAction = input.GetStringPropertyOrEmpty("action");
        var action = NormalizeAction(rawAction);
        // A missing action is not an error: default to list, the harmless read that also shows the
        // model the task indexes it needs for everything else.
        if (string.IsNullOrEmpty(action))
            action = "list";

        var path = Path.Combine(ctx.Cwd, FileName);
        string raw = "";
        if (File.Exists(path))
        {
            try { raw = File.ReadAllText(path); }
            catch (Exception ex) { return Task.FromResult(ToolResult.Err($"Could not read {FileName}: {ex.Message}")); }
        }
        else if (action is "update" or "set_status" or "edit_section" or "delete_section" or "edit_item" or "delete_item")
        {
            // No todo.md yet. Reads return an empty list and add/section bootstrap the file; these
            // actions have nothing to act on, so point the model at the way to start one. Unknown
            // actions fall through to the dispatch below for the usage synopsis.
            return Task.FromResult(ToolResult.Err(
                $"{FileName} not found in {ctx.Cwd}. Use add to start a todo list, e.g. {{\"action\":\"add\",\"text\":\"...\"}}."));
        }

        var lines = raw.Length == 0 ? Array.Empty<string>() : raw.Split('\n');
        var doc = Parse(lines);

        return Task.FromResult(action switch
        {
            "list"           => ListAll(doc, input),
            "add"            => AddTask(path, lines, doc, input),
            "update"         => UpdateTask(path, lines, doc.Tasks, input),
            "section"        => ManageSection(path, lines, doc, input),
            // Legacy action names, kept so calls written against the old nine-verb schema (resumed
            // sessions, cached prompts) keep working. Not advertised in the schema.
            "list_sections"  => ListSections(doc),
            "list_tasks"     => ListTasks(doc.Tasks, input),
            "set_status"     => SetStatus(path, lines, doc.Tasks, input),
            "create_section" => CreateSection(path, lines, doc, input),
            "edit_section"   => EditSection(path, lines, doc, input),
            "delete_section" => DeleteSection(path, lines, doc, input),
            "create_item"    => CreateItem(path, lines, doc, input),
            "edit_item"      => EditItem(path, lines, doc.Tasks, input),
            "delete_item"    => DeleteItem(path, lines, doc.Tasks, input),
            _                => ToolResult.Err($"Unknown action '{rawAction}'.\n{Usage}"),
        });
    }

    private sealed record TodoDocument(List<TodoSection> Sections, List<TodoTask> Tasks);
    private sealed record TodoSection(int Index, string Title, int Level, int LineNumber);
    private sealed record TodoTask(int Index, string Section, bool Done, string Text, int LineNumber);

    private static TodoDocument Parse(string[] lines)
    {
        var sections = new List<TodoSection>();
        var tasks = new List<TodoTask>();
        var section = "(no section)";
        var sectionIndex = 0;
        var index = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');

            var header = HeaderRegex().Match(line);
            if (header.Success)
            {
                section = header.Groups[2].Value.Trim();
                sections.Add(new TodoSection(++sectionIndex, section, header.Groups[1].Value.Length, i));
                continue;
            }

            var task = TaskRegex().Match(line);
            if (task.Success)
            {
                var done = task.Groups[1].Value is "x" or "X";
                tasks.Add(new TodoTask(++index, section, done, task.Groups[2].Value.Trim(), i));
            }
        }

        return new TodoDocument(sections, tasks);
    }

    private static ToolResult ListSections(TodoDocument doc)
    {
        if (doc.Sections.Count == 0 && doc.Tasks.Count == 0)
            return ToolResult.Ok($"{FileName} has no tasks.");

        var sb = new StringBuilder();
        sb.AppendLine($"Sections in {FileName}:");
        var displayIndex = 0;
        foreach (var section in TaskSections(doc))
        {
            var tasks = doc.Tasks.Where(t => t.Section == section.Title).ToList();
            var done = tasks.Count(t => t.Done);
            var todo = tasks.Count(t => !t.Done);
            sb.AppendLine($"[{++displayIndex}] {section.Title} ({todo} todo, {done} done)");
        }

        var noSectionTasks = doc.Tasks.Where(t => t.Section == "(no section)").ToList();
        if (noSectionTasks.Count > 0)
        {
            var done = noSectionTasks.Count(t => t.Done);
            var todo = noSectionTasks.Count(t => !t.Done);
            sb.AppendLine($"- (no section) ({todo} todo, {done} done)");
        }

        sb.Append("Use list_tasks with a section to see individual tasks.");
        return ToolResult.Ok(sb.ToString());
    }

    private static ToolResult ListTasks(List<TodoTask> tasks, JsonElement input)
    {
        var status = NormalizeAction(input.GetStringPropertyOrEmpty("status"));
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

    // ── simplified actions ────────────────────────────────────────────────────

    private const string Usage = """
        Usage:
          {"action":"list"}                                    - all sections and tasks with indexes; optional "section" and "status" (todo|done|all)
          {"action":"add","text":"Fix crash","section":"Bugs"} - add a task; "section" is optional and is created if missing
          {"action":"update","task":3,"done":true}             - update a task by index; "text" rewrites it, "delete":true removes it
          {"action":"section","title":"Phase 2"}               - create a section; "section"+"title" renames, "section"+"delete":true deletes
        """;

    private static ToolResult UsageError(string message) =>
        ToolResult.Err($"{message}\n{Usage}");

    /// <summary>One call shows the whole picture: every section with its tasks and their indexes.</summary>
    private static ToolResult ListAll(TodoDocument doc, JsonElement input)
    {
        if (doc.Sections.Count == 0 && doc.Tasks.Count == 0)
            return ToolResult.Ok($"{FileName} has no tasks. Use {{\"action\":\"add\",\"text\":\"...\"}} to create one.");

        var status = NormalizeAction(input.GetStringPropertyOrEmpty("status"));
        if (string.IsNullOrEmpty(status)) status = "all";
        Func<TodoTask, bool> statusOk = status switch
        {
            "done" => t => t.Done,
            "todo" => t => !t.Done,
            _      => _ => true,
        };

        var sectionFilter = input.GetStringPropertyOrEmpty("section");
        TodoSection? only = null;
        if (!string.IsNullOrWhiteSpace(sectionFilter))
        {
            only = FindSection(doc, sectionFilter);
            if (only is null)
                return ToolResult.Err($"No section matching '{sectionFilter}'. Call {{\"action\":\"list\"}} without 'section' to see all sections.");
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Tasks in {FileName}" + (status == "all" ? "" : $" (status={status})") + ":");
        foreach (var section in TaskSections(doc))
        {
            if (only is not null && section != only)
                continue;

            var all = doc.Tasks.Where(t => t.Section == section.Title).ToList();
            sb.AppendLine($"{section.Title} ({all.Count(t => !t.Done)} todo, {all.Count(t => t.Done)} done)");
            foreach (var t in all.Where(statusOk))
                sb.AppendLine($"  [{t.Index}] {(t.Done ? "[x]" : "[ ]")} {t.Text}");
        }

        if (only is null)
        {
            var loose = doc.Tasks.Where(t => t.Section == "(no section)" && statusOk(t)).ToList();
            if (loose.Count > 0)
            {
                sb.AppendLine("(no section)");
                foreach (var t in loose)
                    sb.AppendLine($"  [{t.Index}] {(t.Done ? "[x]" : "[ ]")} {t.Text}");
            }
        }

        sb.Append("Reference a task by its [index], e.g. {\"action\":\"update\",\"task\":1,\"done\":true}.");
        return ToolResult.Ok(sb.ToString());
    }

    /// <summary>Adds a task. The target section is optional and is created when it does not exist.</summary>
    private static ToolResult AddTask(string path, string[] lines, TodoDocument doc, JsonElement input)
    {
        var text = ItemText(input);
        if (string.IsNullOrWhiteSpace(text))
            return UsageError("add requires 'text' (the task text)");

        var done = FlexBool(input, "done") == true;
        var item = $"- [{(done ? "x" : " ")}] {NormalizeTaskText(text)}";

        var sectionFilter = input.GetStringPropertyOrEmpty("section").Trim();
        var section = string.IsNullOrWhiteSpace(sectionFilter)
            ? TaskSections(doc).LastOrDefault()          // no section given: append to the last one
            : FindSection(doc, sectionFilter);

        if (section is not null)
        {
            var insertAt = LastTaskLineInSection(doc.Tasks, section.Title) is { } lastTaskLine
                ? lastTaskLine + 1
                : section.LineNumber + 1;
            return InsertLines(lines, insertAt, [item], path, $"Added task to {section.Title}: {NormalizeTaskText(text)}");
        }

        // No matching section (or an empty file): create one at the end rather than bouncing the call.
        var title = string.IsNullOrWhiteSpace(sectionFilter) ? "Tasks" : sectionFilter;
        var additions = new List<string>();
        if (lines.Length > 0 && !string.IsNullOrWhiteSpace(lines[^1].TrimEnd('\r')))
            additions.Add("");
        additions.Add($"## {title}");
        additions.Add(item);
        return InsertLines(lines, lines.Length, additions, path,
            $"Created section {title} and added task: {NormalizeTaskText(text)}");
    }

    /// <summary>Rewrites, completes/reopens, or deletes one task addressed by its list index.</summary>
    private static ToolResult UpdateTask(string path, string[] lines, List<TodoTask> tasks, JsonElement input)
    {
        var taskIndex = FlexTaskIndex(input);
        if (taskIndex is null)
            return UsageError("update requires 'task' (the task index shown by list)");

        var target = tasks.FirstOrDefault(t => t.Index == taskIndex);
        if (target is null)
            return ToolResult.Err($"No task with index {taskIndex}. Call {{\"action\":\"list\"}} to see valid indices.");

        if (FlexBool(input, "delete") == true)
        {
            var updated = lines.Where((_, i) => i != target.LineNumber).ToArray();
            return WriteLines(path, updated, $"Deleted task {taskIndex}: {target.Text}");
        }

        var text = ItemText(input);
        var done = FlexBool(input, "done");
        if (string.IsNullOrWhiteSpace(text) && done is null)
            return UsageError("update needs 'text' (new task text), 'done' (true/false), or 'delete': true");

        var line = lines[target.LineNumber];
        var cr = line.EndsWith('\r');
        var core = cr ? line[..^1] : line;
        if (!string.IsNullOrWhiteSpace(text))
            core = TaskTextRegex().Replace(core, $"$1{NormalizeTaskText(text)}", 1);
        if (done is { } d)
            core = CheckboxRegex().Replace(core, d ? "[x]" : "[ ]", 1);
        lines[target.LineNumber] = cr ? core + "\r" : core;

        var changes = new List<string>();
        if (!string.IsNullOrWhiteSpace(text)) changes.Add($"text -> {NormalizeTaskText(text)}");
        if (done is { } dd) changes.Add(dd ? "marked done" : "marked todo");
        return WriteLines(path, lines, $"Task {taskIndex} updated: {string.Join(", ", changes)}");
    }

    /// <summary>Creates, renames, or deletes a section heading in one action.</summary>
    private static ToolResult ManageSection(string path, string[] lines, TodoDocument doc, JsonElement input)
    {
        var sectionFilter = input.GetStringPropertyOrEmpty("section").Trim();
        var title = input.GetStringPropertyOrEmpty("title").Trim();

        if (FlexBool(input, "delete") == true)
        {
            var target = string.IsNullOrWhiteSpace(sectionFilter) ? title : sectionFilter;
            if (string.IsNullOrWhiteSpace(target))
                return UsageError("section delete requires 'section' (title, leading number, or index)");

            var doomed = FindSection(doc, target);
            if (doomed is null)
                return ToolResult.Err($"No section matching '{target}'. Call {{\"action\":\"list\"}} to see sections.");

            var end = NextSectionLine(doc.Sections, doomed.LineNumber) ?? lines.Length;
            var updated = lines.Take(doomed.LineNumber).Concat(lines.Skip(end)).ToArray();
            return WriteLines(path, updated, $"Deleted section: {doomed.Title}");
        }

        var existing = string.IsNullOrWhiteSpace(sectionFilter) ? null : FindSection(doc, sectionFilter);

        if (existing is not null && !string.IsNullOrWhiteSpace(title))
        {
            var line = lines[existing.LineNumber];
            var cr = line.EndsWith('\r');
            lines[existing.LineNumber] = $"{new string('#', existing.Level)} {title}" + (cr ? "\r" : "");
            return WriteLines(path, lines, $"Renamed section '{existing.Title}' to '{title}'");
        }

        if (existing is not null)
            return ToolResult.Ok($"Section already exists: {existing.Title}. Provide 'title' to rename it or 'delete': true to remove it.");

        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(sectionFilter))
            return UsageError("section requires 'title' (the heading text)");

        // Create: CreateSection already handles title/section fallback, duplicates, level, and
        // after_section placement.
        return CreateSection(path, lines, doc, input);
    }

    // Models sometimes send booleans as strings and task indexes as strings; accept both rather
    // than bouncing the call.
    private static bool? FlexBool(JsonElement input, string name)
    {
        if (!input.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }

    private static int? FlexTaskIndex(JsonElement input)
    {
        if (!input.TryGetProperty("task", out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed)) return parsed;
        return null;
    }

    private static string SectionSuffix(JsonElement input)
    {
        var section = input.GetStringPropertyOrEmpty("section");
        return string.IsNullOrWhiteSpace(section) ? "" : $" in {section}";
    }

    private static ToolResult SetStatus(string path, string[] lines, List<TodoTask> tasks, JsonElement input)
    {
        if (FlexTaskIndex(input) is not { } taskIndex)
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

    private static ToolResult CreateSection(string path, string[] lines, TodoDocument doc, JsonElement input)
    {
        var title = input.GetStringPropertyOrEmpty("title").Trim();
        // Models frequently pass the new heading under 'section' (the identifier key used by every
        // other action) instead of 'title'; accept it here rather than bouncing the call.
        if (string.IsNullOrWhiteSpace(title))
            title = input.GetStringPropertyOrEmpty("section").Trim();
        if (string.IsNullOrWhiteSpace(title))
            return ToolResult.Err("create_section requires 'title' (the section heading text)");

        if (doc.Sections.Any(s => MatchesSection(s.Title, title)))
            return ToolResult.Err($"Section already exists: {title}");

        var requestedLevel = GetInt(input, "level");
        var level = requestedLevel is >= 1 and <= 6 ? requestedLevel.Value : 2;
        var after = input.GetStringPropertyOrEmpty("after_section");
        var insertAt = lines.Length;
        if (!string.IsNullOrWhiteSpace(after))
        {
            var afterSection = FindSection(doc, after);
            if (afterSection is null)
                return ToolResult.Err($"No section matching '{after}'. Call list_sections to see valid sections.");

            insertAt = NextSectionLine(doc.Sections, afterSection.LineNumber) ?? lines.Length;
        }

        var additions = new List<string>();
        if (insertAt > 0 && !string.IsNullOrWhiteSpace(lines[Math.Min(insertAt, lines.Length) - 1].TrimEnd('\r')))
            additions.Add("");
        additions.Add($"{new string('#', level)} {title}");
        additions.Add("");

        return InsertLines(lines, insertAt, additions, path, $"Created section: {title}");
    }

    private static ToolResult EditSection(string path, string[] lines, TodoDocument doc, JsonElement input)
    {
        var sectionFilter = input.GetStringPropertyOrEmpty("section");
        var title = input.GetStringPropertyOrEmpty("title").Trim();
        if (string.IsNullOrWhiteSpace(sectionFilter))
            return ToolResult.Err("edit_section requires 'section'");
        if (string.IsNullOrWhiteSpace(title))
            return ToolResult.Err("edit_section requires 'title'");

        var section = FindSection(doc, sectionFilter);
        if (section is null)
            return ToolResult.Err($"No section matching '{sectionFilter}'. Call list_sections to see valid sections.");

        var line = lines[section.LineNumber];
        var cr = line.EndsWith('\r');
        lines[section.LineNumber] = $"{new string('#', section.Level)} {title}" + (cr ? "\r" : "");
        return WriteLines(path, lines, $"Renamed section '{section.Title}' to '{title}'");
    }

    private static ToolResult DeleteSection(string path, string[] lines, TodoDocument doc, JsonElement input)
    {
        var sectionFilter = input.GetStringPropertyOrEmpty("section");
        if (string.IsNullOrWhiteSpace(sectionFilter))
            return ToolResult.Err("delete_section requires 'section'");

        var section = FindSection(doc, sectionFilter);
        if (section is null)
            return ToolResult.Err($"No section matching '{sectionFilter}'. Call list_sections to see valid sections.");

        var end = NextSectionLine(doc.Sections, section.LineNumber) ?? lines.Length;
        var updated = lines.Take(section.LineNumber).Concat(lines.Skip(end)).ToArray();
        return WriteLines(path, updated, $"Deleted section: {section.Title}");
    }

    private static ToolResult CreateItem(string path, string[] lines, TodoDocument doc, JsonElement input)
    {
        var sectionFilter = input.GetStringPropertyOrEmpty("section");
        var text = ItemText(input);
        if (string.IsNullOrWhiteSpace(sectionFilter))
            return ToolResult.Err("create_item requires 'section'");
        if (string.IsNullOrWhiteSpace(text))
            return ToolResult.Err("create_item requires 'text' (the task text)");

        var section = FindSection(doc, sectionFilter);
        if (section is null)
            return ToolResult.Err($"No section matching '{sectionFilter}'. Call list_sections to see valid sections.");

        var done = input.TryGetProperty("done", out var doneEl) && doneEl.ValueKind == JsonValueKind.True;
        var insertAt = LastTaskLineInSection(doc.Tasks, section.Title) is { } lastTaskLine
            ? lastTaskLine + 1
            : section.LineNumber + 1;

        return InsertLines(
            lines,
            insertAt,
            [$"- [{(done ? "x" : " ")}] {NormalizeTaskText(text)}"],
            path,
            $"Created task in {section.Title}: {NormalizeTaskText(text)}");
    }

    private static ToolResult EditItem(string path, string[] lines, List<TodoTask> tasks, JsonElement input)
    {
        if (FlexTaskIndex(input) is not { } taskIndex)
            return ToolResult.Err("edit_item requires 'task' (the 1-based index from list_tasks)");

        var text = ItemText(input);
        if (string.IsNullOrWhiteSpace(text))
            return ToolResult.Err("edit_item requires 'text' (the task text)");

        var target = tasks.FirstOrDefault(t => t.Index == taskIndex);
        if (target is null)
            return ToolResult.Err($"No task with index {taskIndex}. Call list_tasks to see valid indices.");

        var line = lines[target.LineNumber];
        var cr = line.EndsWith('\r');
        var core = cr ? line[..^1] : line;
        core = TaskTextRegex().Replace(core, $"$1{NormalizeTaskText(text)}", 1);
        lines[target.LineNumber] = cr ? core + "\r" : core;
        return WriteLines(path, lines, $"Edited task {taskIndex}: {NormalizeTaskText(text)}");
    }

    private static ToolResult DeleteItem(string path, string[] lines, List<TodoTask> tasks, JsonElement input)
    {
        if (FlexTaskIndex(input) is not { } taskIndex)
            return ToolResult.Err("delete_item requires 'task' (the 1-based index from list_tasks)");

        var target = tasks.FirstOrDefault(t => t.Index == taskIndex);
        if (target is null)
            return ToolResult.Err($"No task with index {taskIndex}. Call list_tasks to see valid indices.");

        var updated = lines.Where((_, i) => i != target.LineNumber).ToArray();
        return WriteLines(path, updated, $"Deleted task {taskIndex}: {target.Text}");
    }

    private static TodoSection? FindSection(TodoDocument doc, string filter)
    {
        var sections = TaskSections(doc).ToList();
        var section = sections.FirstOrDefault(s => MatchesSection(s.Title, filter));
        if (section is not null)
            return section;

        if (int.TryParse(filter.Trim(), out var index) && index >= 1 && index <= sections.Count)
            return sections[index - 1];

        return null;
    }

    private static IEnumerable<TodoSection> TaskSections(TodoDocument doc) =>
        doc.Sections.Where(section =>
            section.Level > 1
            || doc.Tasks.Any(t => t.Section == section.Title)
            || !doc.Sections.Any(other => other.Level > section.Level && other.LineNumber > section.LineNumber));

    private static int? LastTaskLineInSection(List<TodoTask> tasks, string section) =>
        tasks.Where(t => t.Section == section).Select(t => (int?)t.LineNumber).Max();

    private static int? NextSectionLine(List<TodoSection> sections, int lineNumber) =>
        sections.Where(s => s.LineNumber > lineNumber).Select(s => (int?)s.LineNumber).Min();

    private static string NormalizeTaskText(string text)
    {
        var match = TaskRegex().Match(text.Trim());
        return match.Success ? match.Groups[2].Value.Trim() : text.Trim();
    }

    // Task text lives under 'text', but models routinely put it under 'title' (the key
    // create_section/edit_section use for their heading string); accept either.
    private static string ItemText(JsonElement input)
    {
        var text = input.GetStringPropertyOrEmpty("text").Trim();
        return string.IsNullOrWhiteSpace(text) ? input.GetStringPropertyOrEmpty("title").Trim() : text;
    }

    private static int? GetInt(JsonElement input, string propertyName) =>
        input.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var parsed) ? parsed : null;

    private static string NormalizeAction(string value) =>
        value.Trim().ToLowerInvariant();

    private static string InputValue(JsonElement input, string propertyName) =>
        input.TryGetProperty(propertyName, out var value) ? value.ToString() : "?";

    private static ToolResult InsertLines(string[] lines, int index, IEnumerable<string> additions, string path, string success)
    {
        var updated = lines.Take(index).Concat(additions).Concat(lines.Skip(index)).ToArray();
        return WriteLines(path, updated, success);
    }

    private static ToolResult WriteLines(string path, string[] lines, string success)
    {
        try { File.WriteAllText(path, string.Join('\n', lines)); }
        catch (Exception ex) { return ToolResult.Err($"Could not write {FileName}: {ex.Message}"); }

        return ToolResult.Ok(success);
    }

    private static bool MatchesSection(string section, string filter)
    {
        filter = filter.Trim();
        if (section.ContainsNoCase(filter))
            return true;
        // Match by leading number, e.g. filter "2" against "2. Bug fixes".
        var num = LeadingNumberRegex().Match(section);
        return num.Success && num.Groups[1].Value == filter;
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.+?)\s*$")]
    private static partial Regex HeaderRegex();

    [GeneratedRegex(@"^\s*[-*]\s+\[([ xX])\]\s*(.*)$")]
    private static partial Regex TaskRegex();

    [GeneratedRegex(@"\[[ xX]\]")]
    private static partial Regex CheckboxRegex();

    [GeneratedRegex(@"^(\s*[-*]\s+\[[ xX]\]\s*).*$")]
    private static partial Regex TaskTextRegex();

    [GeneratedRegex(@"^\s*(\d+)\.")]
    private static partial Regex LeadingNumberRegex();
}
