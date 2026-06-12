using System.Text.Json;
using Dotsy.Core.Loop;
using Dotsy.Core.Tools;

namespace Dotsy.Core.Tests;

[TestClass]
public sealed class EditToolTests
{
    private string _tmpDir = "";

    [TestInitialize]
    public void Setup()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"dotsy_edit_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
    }

    [TestCleanup]
    public void Teardown()
    {
        if (Directory.Exists(_tmpDir))
            Directory.Delete(_tmpDir, recursive: true);
    }

    private ToolContext Ctx() => new()
    {
        Cwd = _tmpDir,
        LoopContext = new LoopContext()
    };

    private static JsonElement Args(string path, string oldStr, string newStr, bool? replaceAll = null)
    {
        var obj = replaceAll.HasValue
            ? $"{{\"path\":\"{EscJson(path)}\",\"old_string\":\"{EscJson(oldStr)}\",\"new_string\":\"{EscJson(newStr)}\",\"replace_all\":{(replaceAll.Value ? "true" : "false")}}}"
            : $"{{\"path\":\"{EscJson(path)}\",\"old_string\":\"{EscJson(oldStr)}\",\"new_string\":\"{EscJson(newStr)}\"}}";
        return JsonDocument.Parse(obj).RootElement;
    }

    private static JsonElement LineArgs(string path, int startLine, int endLine, string newStr) =>
        JsonDocument.Parse($"{{\"path\":\"{EscJson(path)}\",\"start_line\":{startLine},\"end_line\":{endLine},\"new_string\":\"{EscJson(newStr)}\"}}").RootElement;

    private static string EscJson(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");

    // ── old_string not found ─────────────────────────────────────────────────

    [TestMethod]
    public async Task FailsWhenOldStringNotFound()
    {
        var file = Path.Combine(_tmpDir, "test.cs");
        await File.WriteAllTextAsync(file, "Hello world");

        var tool   = new EditTool();
        var result = await tool.ExecuteAsync(Args(file, "NOTHERE", "replacement"), Ctx(), CancellationToken.None);

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.Content, "not found");
    }

    // ── old_string not unique without replace_all ─────────────────────────────

    [TestMethod]
    public async Task FailsWhenOldStringNotUnique_WithoutReplaceAll()
    {
        var file = Path.Combine(_tmpDir, "test.cs");
        await File.WriteAllTextAsync(file, "foo bar foo");

        var tool   = new EditTool();
        var result = await tool.ExecuteAsync(Args(file, "foo", "baz"), Ctx(), CancellationToken.None);

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.Content, "not unique");
    }

    // ── replace_all replaces every occurrence ────────────────────────────────

    [TestMethod]
    public async Task ReplaceAll_ReplacesEveryOccurrence()
    {
        var file = Path.Combine(_tmpDir, "test.cs");
        await File.WriteAllTextAsync(file, "foo bar foo baz foo");

        var tool   = new EditTool();
        var result = await tool.ExecuteAsync(Args(file, "foo", "qux", replaceAll: true), Ctx(), CancellationToken.None);

        Assert.IsFalse(result.IsError, result.Content);
        var text = await File.ReadAllTextAsync(file);
        Assert.AreEqual("qux bar qux baz qux", text);
    }

    // ── successful single replacement ────────────────────────────────────────

    [TestMethod]
    public async Task SucceedsForUniqueOldString()
    {
        var file = Path.Combine(_tmpDir, "test.cs");
        await File.WriteAllTextAsync(file, "Hello world");

        var tool   = new EditTool();
        var result = await tool.ExecuteAsync(Args(file, "world", "dotsy"), Ctx(), CancellationToken.None);

        Assert.IsFalse(result.IsError, result.Content);
        Assert.AreEqual("Hello dotsy", await File.ReadAllTextAsync(file));
    }

    // ── line-range: replace middle lines ─────────────────────────────────────

    [TestMethod]
    public async Task LineRange_ReplacesMiddleLines()
    {
        var file = Path.Combine(_tmpDir, "test.cs");
        await File.WriteAllTextAsync(file, "line1\nline2\nline3\nline4\n");

        var tool   = new EditTool();
        var result = await tool.ExecuteAsync(LineArgs(file, 2, 3, "replaced2\nreplaced3"), Ctx(), CancellationToken.None);

        Assert.IsFalse(result.IsError, result.Content);
        Assert.AreEqual("line1\nreplaced2\nreplaced3\nline4\n", await File.ReadAllTextAsync(file));
    }

    // ── line-range: replace single line ──────────────────────────────────────

    [TestMethod]
    public async Task LineRange_ReplacesSingleLine()
    {
        var file = Path.Combine(_tmpDir, "test.cs");
        await File.WriteAllTextAsync(file, "aaa\nbbb\nccc\n");

        var tool   = new EditTool();
        var result = await tool.ExecuteAsync(LineArgs(file, 2, 2, "NEW"), Ctx(), CancellationToken.None);

        Assert.IsFalse(result.IsError, result.Content);
        Assert.AreEqual("aaa\nNEW\nccc\n", await File.ReadAllTextAsync(file));
    }

    // ── line-range: out of bounds ─────────────────────────────────────────────

    [TestMethod]
    public async Task LineRange_FailsWhenOutOfBounds()
    {
        var file = Path.Combine(_tmpDir, "test.cs");
        await File.WriteAllTextAsync(file, "only one line\n");

        var tool   = new EditTool();
        var result = await tool.ExecuteAsync(LineArgs(file, 5, 6, "x"), Ctx(), CancellationToken.None);

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.Content, "out of bounds");
    }

    // ── line-range: missing end_line ─────────────────────────────────────────

    [TestMethod]
    public async Task LineRange_FailsWhenOnlyStartLineGiven()
    {
        var file = Path.Combine(_tmpDir, "test.cs");
        await File.WriteAllTextAsync(file, "aaa\nbbb\n");

        var args = JsonDocument.Parse($"{{\"path\":\"{EscJson(file)}\",\"start_line\":1,\"new_string\":\"x\"}}").RootElement;
        var tool   = new EditTool();
        var result = await tool.ExecuteAsync(args, Ctx(), CancellationToken.None);

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.Content, "both be provided");
    }
}
