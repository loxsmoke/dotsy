using System.Text.Json;
using System.Text;
using Dotsy.Core.Utils;
using Dotsy.Core.Tools.Interfaces;

namespace Dotsy.Core.Tools;

public sealed class MultiEditTool : ITool
{
    public const string ToolName = "MultiEdit";
    public string Name => ToolName;
    public string Description => "Apply multiple non-overlapping edits to a file in one call. Read the file first. Each edit replaces an exact 1-based inclusive start_line/end_line range. All line numbers refer to the file as you read it — edits never shift each other's ranges.";
    public JsonElement InputSchema => ToolSchemas.MultiEditSchema;
    public ToolSafety Safety => ToolSafety.Sequential;
    public bool IsCompletionSignal => false;
    public bool IsWriteTool => true;

    public string FormatRunApproval(JsonElement input, string cwd)
    {
        var path = input.GetStringPropertyOrEmpty("path");
        var fileName = Path.GetFileName(path);
        var count = input.TryGetProperty("edits", out var e) && e.ValueKind == JsonValueKind.Array
            ? e.GetArrayLength()
            : 0;
        return $"{fileName}  {count} edit{(count == 1 ? "" : "s")}";
    }

    public string FormatPanelArgument(JsonElement input, string cwd)
    {
        var path = PathDisplay.MakeRelative(input.GetStringPropertyOrEmpty("path"), cwd);
        var count = input.TryGetProperty("edits", out var editsEl) && editsEl.ValueKind == JsonValueKind.Array
            ? editsEl.GetArrayLength()
            : 0;

        int totalAdded = 0;
        int totalDeleted = 0;
        if (input.TryGetProperty("edits", out var edits))
        {
            foreach (var edit in edits.EnumerateArray())
            {
                var newString = edit.GetStringPropertyOrEmpty("new_string");
                if (edit.TryGetProperty("start_line", out var sl) && edit.TryGetProperty("end_line", out var el))
                {
                    totalDeleted += el.GetInt32() - sl.GetInt32() + 1;
                    totalAdded += EditSummaryFormatter.CountLines(newString);
                }
                else
                {
                    var oldString = edit.GetStringPropertyOrEmpty("old_string");
                    totalDeleted += EditSummaryFormatter.CountLines(oldString);
                    totalAdded += EditSummaryFormatter.CountLines(newString);
                }
            }
        }

        return $"{path}  {count} edit{(count == 1 ? "" : "s")}{EditSummaryFormatter.LineDelta(totalAdded, totalDeleted)}";
    }

    public string? FormatPanelResult(JsonElement input, string resultContent, string cwd)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(resultContent))
        {
            sb.AppendLine("Output");
            sb.AppendLine(resultContent.TrimEnd());
            sb.AppendLine();
        }

        sb.AppendLine("Input");
        sb.AppendLine($"path: {PathDisplay.MakeRelative(input.GetStringPropertyOrEmpty("path"), cwd)}");

        if (input.TryGetProperty("edits", out var edits) && edits.ValueKind == JsonValueKind.Array)
        {
            var index = 1;
            foreach (var edit in edits.EnumerateArray())
            {
                sb.AppendLine();
                EditTool.AppendEditInput(sb, edit, $"Edit {index}", cwd);
                index++;
            }
        }

        return sb.ToString().TrimEnd();
    }

    public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var path = ReadTool.ResolvePath(input, ctx.Cwd);

        if (!File.Exists(path))
            return Task.FromResult(ToolResult.Err($"File not found: {path}"));

        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) { return Task.FromResult(ToolResult.Err(ex.Message)); }

        var (edits, editsError) = ResolveEdits(input);
        if (editsError is not null)
            return Task.FromResult(ToolResult.Err(editsError));

        // The model supplies every line range in the coordinates of the file it read, so line
        // edits must not see each other's shifts: validate overlaps against the original file,
        // then apply bottom-up (descending start_line) so earlier applications never move the
        // lines a later edit targets. Applying top-down walked ranges off the end of the buffer
        // ("out of bounds (file has N lines)" while the file on disk still had the lines) or,
        // when the shifted range stayed in bounds, silently edited the wrong lines.
        var lineEdits = new List<(int Ordinal, int Start, int End, string NewString)>();
        var textEdits = new List<(int Ordinal, JsonElement Edit)>();
        int ordinal = 0;
        foreach (var edit in edits!)
        {
            ordinal++;
            bool hasStartLine = edit.TryGetProperty("start_line", out var startEl);
            bool hasEndLine   = edit.TryGetProperty("end_line",   out var endEl);

            if (hasStartLine || hasEndLine)
            {
                if (!hasStartLine || !hasEndLine)
                    return Task.FromResult(ToolResult.Err($"Edit {ordinal}: start_line and end_line must both be provided"));
                lineEdits.Add((ordinal, startEl.GetInt32(), endEl.GetInt32(),
                    edit.GetProperty("new_string").GetString() ?? ""));
            }
            else
            {
                textEdits.Add((ordinal, edit));
            }
        }

        var byStart = lineEdits.OrderBy(e => e.Start).ToList();
        for (int i = 1; i < byStart.Count; i++)
        {
            var prev = byStart[i - 1];
            var cur  = byStart[i];
            if (cur.Start <= prev.End)
                return Task.FromResult(ToolResult.Err(
                    $"Edit {cur.Ordinal}: line range {cur.Start}-{cur.End} overlaps Edit {prev.Ordinal} " +
                    $"({prev.Start}-{prev.End}); line ranges refer to the original file and must not overlap"));
        }

        int applied = 0;
        for (int i = byStart.Count - 1; i >= 0; i--)
        {
            var e = byStart[i];
            var result = EditTool.ApplyLineRange(text, e.Start, e.End, e.NewString);
            if (result.IsError)
                return Task.FromResult(ToolResult.Err($"Edit {e.Ordinal}: {result.Content}"));
            text = result.Content;
            applied++;
        }

        // old_string edits locate their target by content, not position — apply in given order.
        foreach (var (ord, edit) in textEdits)
        {
            var newString = edit.GetProperty("new_string").GetString() ?? "";
            var oldString = edit.GetStringPropertyOrEmpty("old_string");
            bool replaceAll = edit.TryGetProperty("replace_all", out var ra) && ra.GetBoolean();

            var result = EditTool.ApplyTextReplace(text, oldString, newString, replaceAll);
            if (result.IsError)
                return Task.FromResult(ToolResult.Err($"Edit {ord}: {result.Content}"));
            text = result.Content;
            applied++;
        }

        try
        {
            File.WriteAllText(path, text);
            return Task.FromResult(ToolResult.Ok($"Applied {applied} edit(s) to: {path}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Err(ex.Message));
        }
    }

    // Resolves the `edits` argument into a list of edit objects, tolerating models that send it as
    // a JSON-encoded string instead of an array (the cause of the opaque "requires Array, got String"
    // crash). Returns a clear error message instead of throwing on a bad shape. Elements are cloned
    // so they stay valid independent of any temporary document parsed here.
    private static (List<JsonElement>? Edits, string? Error) ResolveEdits(JsonElement input)
    {
        if (!input.TryGetProperty("edits", out var editsEl))
            return (null, "edits array is required");

        JsonElement arrayEl;
        if (editsEl.ValueKind == JsonValueKind.Array)
        {
            arrayEl = editsEl;
        }
        else if (editsEl.ValueKind == JsonValueKind.String)
        {
            // Fallback: tolerate `edits` sent as a JSON-encoded string ("edits": "[{...}]"), including
            // models that double/triple-encode it. Unwrap each string layer by parsing it as JSON
            // until we reach an array. The keepalive docs prevent the parsed roots from being disposed.
            var docs = new List<JsonDocument>();
            try
            {
                var parsed = editsEl;
                for (int depth = 0; depth < 4 && parsed.ValueKind == JsonValueKind.String; depth++)
                {
                    var s = parsed.GetString();
                    if (string.IsNullOrWhiteSpace(s))
                        return (null, "edits is an empty string; expected an array of edit objects");

                    JsonDocument doc;
                    try { doc = JsonDocument.Parse(s); }
                    catch
                    {
                        return (null, "edits must be an array of edit objects. It was sent as a string that "
                            + "is not valid JSON — send a JSON array of {start_line, end_line, new_string}, "
                            + "not a quoted string.");
                    }
                    docs.Add(doc);
                    parsed = doc.RootElement;
                }

                if (parsed.ValueKind != JsonValueKind.Array)
                    return (null, $"edits must be an array of edit objects; the provided string decoded to {parsed.ValueKind}");

                // Clone out so the elements outlive the temporary documents.
                var fromString = new List<JsonElement>();
                foreach (var e in parsed.EnumerateArray())
                    fromString.Add(e.Clone());
                return (fromString, null);
            }
            finally
            {
                foreach (var d in docs) d.Dispose();
            }
        }
        else
        {
            return (null, $"edits must be an array of edit objects; got {editsEl.ValueKind}");
        }

        var list = new List<JsonElement>();
        foreach (var e in arrayEl.EnumerateArray())
            list.Add(e.Clone());
        return (list, null);
    }

}
