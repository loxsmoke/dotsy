using System.Text.Json;
using Dotsy.Core.Loop;
using Dotsy.Core.Loop.Data;
using Dotsy.Core.Tools;

namespace Dotsy.Core.Tests;

[TestClass]
public sealed class ReadBeforeEditTests
{
    private string _tmpDir = "";
    private LoopContext _ctx = new();

    [TestInitialize]
    public void Setup()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"dotsy_rbe_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
        _ctx = new LoopContext();
    }

    [TestCleanup]
    public void Teardown()
    {
        if (Directory.Exists(_tmpDir))
            Directory.Delete(_tmpDir, recursive: true);
    }

    private string WriteFile(string name, string content = "line1\nline2\nline3\n")
    {
        var path = Path.Combine(_tmpDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    private static string Esc(string s) => s.Replace("\\", "\\\\");

    private static JsonElement PathArgs(string path) =>
        Json($"{{\"path\":\"{Esc(path)}\"}}");

    private static JsonElement LineEditArgs(string path) =>
        Json($"{{\"path\":\"{Esc(path)}\",\"start_line\":1,\"end_line\":1,\"new_string\":\"x\"}}");

    private static JsonElement StringEditArgs(string path) =>
        Json($"{{\"path\":\"{Esc(path)}\",\"old_string\":\"line1\",\"new_string\":\"x\"}}");

    private static JsonElement MultiEditLineArgs(string path) =>
        Json($"{{\"path\":\"{Esc(path)}\",\"edits\":[{{\"start_line\":1,\"end_line\":1,\"new_string\":\"x\"}}]}}");

    private static JsonElement MultiEditStringArgs(string path) =>
        Json($"{{\"path\":\"{Esc(path)}\",\"edits\":[{{\"old_string\":\"line1\",\"new_string\":\"x\"}}]}}");

    // ── never read ────────────────────────────────────────────────────────────

    [TestMethod]
    public void Edit_WithoutPriorRead_IsRejected()
    {
        var file = WriteFile("a.cs");

        var result = ReadBeforeEdit.Check(_ctx, _tmpDir, EditTool.ToolName, LineEditArgs(file));

        Assert.IsNotNull(result);
        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.Content, "Read");
    }

    [TestMethod]
    public void MultiEdit_WithoutPriorRead_IsRejected()
    {
        var file = WriteFile("a.cs");

        var result = ReadBeforeEdit.Check(_ctx, _tmpDir, MultiEditTool.ToolName, MultiEditLineArgs(file));

        Assert.IsNotNull(result);
        Assert.IsTrue(result.IsError);
    }

    // ── read then edit ────────────────────────────────────────────────────────

    [TestMethod]
    public void Edit_AfterRead_IsAllowed()
    {
        var file = WriteFile("a.cs");
        ReadBeforeEdit.RecordRead(_ctx, _tmpDir, PathArgs(file));

        Assert.IsNull(ReadBeforeEdit.Check(_ctx, _tmpDir, EditTool.ToolName, LineEditArgs(file)));
        Assert.IsNull(ReadBeforeEdit.Check(_ctx, _tmpDir, MultiEditTool.ToolName, MultiEditLineArgs(file)));
    }

    [TestMethod]
    public void Edit_AfterRead_RelativePath_IsAllowed()
    {
        WriteFile("a.cs");
        // Read recorded with an absolute path, edit issued with a relative one — keys must match.
        ReadBeforeEdit.RecordRead(_ctx, _tmpDir, PathArgs(Path.Combine(_tmpDir, "a.cs")));

        Assert.IsNull(ReadBeforeEdit.Check(_ctx, _tmpDir, EditTool.ToolName,
            Json("{\"path\":\"a.cs\",\"start_line\":1,\"end_line\":1,\"new_string\":\"x\"}")));
    }

    // ── external modification ─────────────────────────────────────────────────

    [TestMethod]
    public void Edit_AfterExternalChange_IsRejected()
    {
        var file = WriteFile("a.cs");
        ReadBeforeEdit.RecordRead(_ctx, _tmpDir, PathArgs(file));

        File.WriteAllText(file, "changed externally — different size\n");

        var result = ReadBeforeEdit.Check(_ctx, _tmpDir, EditTool.ToolName, StringEditArgs(file));

        Assert.IsNotNull(result);
        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.Content, "changed on disk");
    }

    // ── own write staleness: line-range vs string-match ──────────────────────

    [TestMethod]
    public void LineRangeEdit_AfterOwnWrite_WithoutReRead_IsRejected()
    {
        var file = WriteFile("a.cs");
        ReadBeforeEdit.RecordRead(_ctx, _tmpDir, PathArgs(file));
        ReadBeforeEdit.RecordWrite(_ctx, _tmpDir, PathArgs(file));

        var result = ReadBeforeEdit.Check(_ctx, _tmpDir, EditTool.ToolName, LineEditArgs(file));

        Assert.IsNotNull(result);
        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.Content, "line numbers");
    }

    [TestMethod]
    public void LineRangeMultiEdit_AfterOwnWrite_WithoutReRead_IsRejected()
    {
        var file = WriteFile("a.cs");
        ReadBeforeEdit.RecordWrite(_ctx, _tmpDir, PathArgs(file));

        var result = ReadBeforeEdit.Check(_ctx, _tmpDir, MultiEditTool.ToolName, MultiEditLineArgs(file));

        Assert.IsNotNull(result);
        Assert.IsTrue(result.IsError);
    }

    [TestMethod]
    public void StringMatchEdit_AfterOwnWrite_IsAllowed()
    {
        var file = WriteFile("a.cs");
        ReadBeforeEdit.RecordWrite(_ctx, _tmpDir, PathArgs(file));

        // old_string edits fail loudly on a stale image, so they don't require a re-read.
        Assert.IsNull(ReadBeforeEdit.Check(_ctx, _tmpDir, EditTool.ToolName, StringEditArgs(file)));
        Assert.IsNull(ReadBeforeEdit.Check(_ctx, _tmpDir, MultiEditTool.ToolName, MultiEditStringArgs(file)));
    }

    [TestMethod]
    public void LineRangeEdit_AfterOwnWriteAndReRead_IsAllowed()
    {
        var file = WriteFile("a.cs");
        ReadBeforeEdit.RecordWrite(_ctx, _tmpDir, PathArgs(file));
        ReadBeforeEdit.RecordRead(_ctx, _tmpDir, PathArgs(file));

        Assert.IsNull(ReadBeforeEdit.Check(_ctx, _tmpDir, EditTool.ToolName, LineEditArgs(file)));
    }

    // ── pass-through cases ────────────────────────────────────────────────────

    [TestMethod]
    public void NonEditTools_AreNotChecked()
    {
        var file = WriteFile("a.cs");

        Assert.IsNull(ReadBeforeEdit.Check(_ctx, _tmpDir, WriteTool.ToolName, PathArgs(file)));
        Assert.IsNull(ReadBeforeEdit.Check(_ctx, _tmpDir, ReadTool.ToolName, PathArgs(file)));
        Assert.IsNull(ReadBeforeEdit.Check(_ctx, _tmpDir, "Shell", Json("{\"command\":\"dir\"}")));
    }

    [TestMethod]
    public void MissingFile_PassesThrough_SoToolReportsFileNotFound()
    {
        var missing = Path.Combine(_tmpDir, "missing.cs");

        Assert.IsNull(ReadBeforeEdit.Check(_ctx, _tmpDir, EditTool.ToolName, LineEditArgs(missing)));
    }

    [TestMethod]
    public void MissingOrEmptyPath_PassesThrough_SoToolReportsItsOwnError()
    {
        Assert.IsNull(ReadBeforeEdit.Check(_ctx, _tmpDir, EditTool.ToolName,
            Json("{\"start_line\":1,\"end_line\":1,\"new_string\":\"x\"}")));
        Assert.IsNull(ReadBeforeEdit.Check(_ctx, _tmpDir, EditTool.ToolName,
            Json("{\"path\":\"\",\"new_string\":\"x\"}")));
        Assert.IsNull(ReadBeforeEdit.Check(_ctx, _tmpDir, EditTool.ToolName, Json("\"not an object\"")));
    }

    [TestMethod]
    public void RecordRead_OfMissingFile_IsIgnored()
    {
        ReadBeforeEdit.RecordRead(_ctx, _tmpDir, PathArgs(Path.Combine(_tmpDir, "missing.cs")));
        Assert.AreEqual(0, _ctx.FileFreshness.Count);
    }
}
