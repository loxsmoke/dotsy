using System.Text.Json;
using Dotsy.Core.Loop.Data;
using Dotsy.Core.Tools;

namespace Dotsy.Core.Tests;

[TestClass]
public sealed class TodoToolTests
{
    private string _tmpDir = "";

    [TestInitialize]
    public void Setup()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"dotsy_todo_test_{Guid.NewGuid():N}");
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

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    private string TodoPath => Path.Combine(_tmpDir, TodoTool.FileName);

    // ── create_section accepts 'section' as a fallback for 'title' ────────────
    // Models frequently pass the heading under 'section' (the identifier key used by every other
    // action) rather than 'title'; the tool should create the section instead of erroring.

    [TestMethod]
    public async Task CreateSection_AcceptsSectionAsTitleFallback()
    {
        var tool = new TodoTool();
        var result = await tool.ExecuteAsync(
            Args("""{"action":"create_section","section":"Phase 1"}"""), Ctx(), CancellationToken.None);

        Assert.IsFalse(result.IsError, result.Content);
        StringAssert.Contains(await File.ReadAllTextAsync(TodoPath), "# Phase 1");
    }

    // ── 'title' wins when both are supplied ───────────────────────────────────

    [TestMethod]
    public async Task CreateSection_PrefersTitleOverSection()
    {
        var tool = new TodoTool();
        var result = await tool.ExecuteAsync(
            Args("""{"action":"create_section","title":"Real Title","section":"Ignored"}"""), Ctx(), CancellationToken.None);

        Assert.IsFalse(result.IsError, result.Content);
        var text = await File.ReadAllTextAsync(TodoPath);
        StringAssert.Contains(text, "# Real Title");
        Assert.IsFalse(text.Contains("Ignored"), "section value must not become the heading when title is present");
    }

    // ── neither 'title' nor 'section' → helpful error, no file written ─────────

    [TestMethod]
    public async Task CreateSection_FailsWhenTitleAndSectionMissing()
    {
        var tool = new TodoTool();
        var result = await tool.ExecuteAsync(
            Args("""{"action":"create_section"}"""), Ctx(), CancellationToken.None);

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.Content, "requires 'title'");
        Assert.IsFalse(File.Exists(TodoPath));
    }

    // ── create_item accepts 'title' as a fallback for 'text' ──────────────────
    // Mirror of the create_section case: models put the task text under 'title' instead of 'text'.

    [TestMethod]
    public async Task CreateItem_AcceptsTitleAsTextFallback()
    {
        var tool = new TodoTool();
        await tool.ExecuteAsync(Args("""{"action":"create_section","title":"Work"}"""), Ctx(), CancellationToken.None);

        var result = await tool.ExecuteAsync(
            Args("""{"action":"create_item","section":"Work","title":"Do the thing"}"""), Ctx(), CancellationToken.None);

        Assert.IsFalse(result.IsError, result.Content);
        StringAssert.Contains(await File.ReadAllTextAsync(TodoPath), "- [ ] Do the thing");
    }

    // ── edit_item accepts 'title' as a fallback for 'text' ────────────────────

    [TestMethod]
    public async Task EditItem_AcceptsTitleAsTextFallback()
    {
        var tool = new TodoTool();
        await tool.ExecuteAsync(Args("""{"action":"create_section","title":"Work"}"""), Ctx(), CancellationToken.None);
        await tool.ExecuteAsync(Args("""{"action":"create_item","section":"Work","text":"original"}"""), Ctx(), CancellationToken.None);

        var result = await tool.ExecuteAsync(
            Args("""{"action":"edit_item","task":1,"title":"renamed"}"""), Ctx(), CancellationToken.None);

        Assert.IsFalse(result.IsError, result.Content);
        StringAssert.Contains(await File.ReadAllTextAsync(TodoPath), "- [ ] renamed");
    }

    // ── create_item still requires a section (no safe fallback) ───────────────

    [TestMethod]
    public async Task CreateItem_FailsWhenSectionMissing()
    {
        var tool = new TodoTool();
        await tool.ExecuteAsync(Args("""{"action":"create_section","title":"Work"}"""), Ctx(), CancellationToken.None);

        var result = await tool.ExecuteAsync(
            Args("""{"action":"create_item","text":"orphan task"}"""), Ctx(), CancellationToken.None);

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.Content, "requires 'section'");
    }

    // ── simplified actions ─────────────────────────────────────────────────────

    [TestMethod]
    public async Task MissingAction_DefaultsToList()
    {
        var tool = new TodoTool();
        var result = await tool.ExecuteAsync(Args("{}"), Ctx(), CancellationToken.None);

        Assert.IsFalse(result.IsError, result.Content);
        StringAssert.Contains(result.Content, "no tasks");
    }

    [TestMethod]
    public async Task List_ShowsSectionsWithTasksAndIndexes()
    {
        await File.WriteAllTextAsync(TodoPath, "## Bugs\n- [ ] fix crash\n- [x] fix typo\n\n## Ideas\n- [ ] dark mode\n");

        var tool = new TodoTool();
        var result = await tool.ExecuteAsync(Args("""{"action":"list"}"""), Ctx(), CancellationToken.None);

        Assert.IsFalse(result.IsError, result.Content);
        StringAssert.Contains(result.Content, "Bugs (1 todo, 1 done)");
        StringAssert.Contains(result.Content, "[1] [ ] fix crash");
        StringAssert.Contains(result.Content, "[2] [x] fix typo");
        StringAssert.Contains(result.Content, "[3] [ ] dark mode");
    }

    [TestMethod]
    public async Task List_StatusFilter_HidesDoneTasks()
    {
        await File.WriteAllTextAsync(TodoPath, "## Bugs\n- [ ] fix crash\n- [x] fix typo\n");

        var tool = new TodoTool();
        var result = await tool.ExecuteAsync(Args("""{"action":"list","status":"todo"}"""), Ctx(), CancellationToken.None);

        Assert.IsFalse(result.IsError, result.Content);
        StringAssert.Contains(result.Content, "fix crash");
        Assert.IsFalse(result.Content.Contains("fix typo"), "done task must be filtered out");
    }

    [TestMethod]
    public async Task Add_WithoutFileOrSection_BootstrapsDefaultSection()
    {
        var tool = new TodoTool();
        var result = await tool.ExecuteAsync(
            Args("""{"action":"add","text":"first task"}"""), Ctx(), CancellationToken.None);

        Assert.IsFalse(result.IsError, result.Content);
        var text = await File.ReadAllTextAsync(TodoPath);
        StringAssert.Contains(text, "## Tasks");
        StringAssert.Contains(text, "- [ ] first task");
    }

    [TestMethod]
    public async Task Add_UnknownSection_AutoCreatesIt()
    {
        await File.WriteAllTextAsync(TodoPath, "## Bugs\n- [ ] fix crash\n");

        var tool = new TodoTool();
        var result = await tool.ExecuteAsync(
            Args("""{"action":"add","text":"dark mode","section":"Ideas"}"""), Ctx(), CancellationToken.None);

        Assert.IsFalse(result.IsError, result.Content);
        var text = await File.ReadAllTextAsync(TodoPath);
        StringAssert.Contains(text, "## Ideas");
        StringAssert.Contains(text, "- [ ] dark mode");
    }

    [TestMethod]
    public async Task Add_WithoutSection_AppendsToLastSection()
    {
        await File.WriteAllTextAsync(TodoPath, "## Bugs\n- [ ] fix crash\n\n## Ideas\n- [ ] dark mode\n");

        var tool = new TodoTool();
        var result = await tool.ExecuteAsync(
            Args("""{"action":"add","text":"light mode"}"""), Ctx(), CancellationToken.None);

        Assert.IsFalse(result.IsError, result.Content);
        StringAssert.Contains(result.Content, "Ideas");
        var text = await File.ReadAllTextAsync(TodoPath);
        Assert.IsTrue(text.IndexOf("light mode") > text.IndexOf("dark mode"), "task must land in the last section");
    }

    [TestMethod]
    public async Task Update_MarksDone_AndAcceptsStringTaskIndex()
    {
        await File.WriteAllTextAsync(TodoPath, "## Bugs\n- [ ] fix crash\n");

        var tool = new TodoTool();
        var result = await tool.ExecuteAsync(
            Args("""{"action":"update","task":"1","done":true}"""), Ctx(), CancellationToken.None);

        Assert.IsFalse(result.IsError, result.Content);
        StringAssert.Contains(await File.ReadAllTextAsync(TodoPath), "- [x] fix crash");
    }

    [TestMethod]
    public async Task Update_RewritesTextAndStatusTogether()
    {
        await File.WriteAllTextAsync(TodoPath, "## Bugs\n- [ ] fix crash\n");

        var tool = new TodoTool();
        var result = await tool.ExecuteAsync(
            Args("""{"action":"update","task":1,"text":"fix startup crash","done":true}"""), Ctx(), CancellationToken.None);

        Assert.IsFalse(result.IsError, result.Content);
        StringAssert.Contains(await File.ReadAllTextAsync(TodoPath), "- [x] fix startup crash");
    }

    [TestMethod]
    public async Task Update_DeleteTrue_RemovesTask()
    {
        await File.WriteAllTextAsync(TodoPath, "## Bugs\n- [ ] fix crash\n- [ ] fix typo\n");

        var tool = new TodoTool();
        var result = await tool.ExecuteAsync(
            Args("""{"action":"update","task":1,"delete":true}"""), Ctx(), CancellationToken.None);

        Assert.IsFalse(result.IsError, result.Content);
        var text = await File.ReadAllTextAsync(TodoPath);
        Assert.IsFalse(text.Contains("fix crash"));
        StringAssert.Contains(text, "fix typo");
    }

    [TestMethod]
    public async Task Update_WithNoChangeFields_ReturnsUsage()
    {
        await File.WriteAllTextAsync(TodoPath, "## Bugs\n- [ ] fix crash\n");

        var tool = new TodoTool();
        var result = await tool.ExecuteAsync(
            Args("""{"action":"update","task":1}"""), Ctx(), CancellationToken.None);

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.Content, "Usage:");
    }

    [TestMethod]
    public async Task Section_CreatesRenamesAndDeletes()
    {
        var tool = new TodoTool();

        var create = await tool.ExecuteAsync(
            Args("""{"action":"section","title":"Phase 1"}"""), Ctx(), CancellationToken.None);
        Assert.IsFalse(create.IsError, create.Content);
        StringAssert.Contains(await File.ReadAllTextAsync(TodoPath), "# Phase 1");

        var rename = await tool.ExecuteAsync(
            Args("""{"action":"section","section":"Phase 1","title":"Phase One"}"""), Ctx(), CancellationToken.None);
        Assert.IsFalse(rename.IsError, rename.Content);
        StringAssert.Contains(await File.ReadAllTextAsync(TodoPath), "# Phase One");

        var delete = await tool.ExecuteAsync(
            Args("""{"action":"section","section":"Phase One","delete":true}"""), Ctx(), CancellationToken.None);
        Assert.IsFalse(delete.IsError, delete.Content);
        Assert.IsFalse((await File.ReadAllTextAsync(TodoPath)).Contains("Phase One"));
    }

    [TestMethod]
    public async Task UnknownAction_ReturnsUsageSynopsis()
    {
        var tool = new TodoTool();
        var result = await tool.ExecuteAsync(
            Args("""{"action":"frobnicate"}"""), Ctx(), CancellationToken.None);

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.Content, "Unknown action 'frobnicate'");
        StringAssert.Contains(result.Content, "Usage:");
    }

    [TestMethod]
    public async Task LegacyActionNames_StillWork()
    {
        await File.WriteAllTextAsync(TodoPath, "## Bugs\n- [ ] fix crash\n");

        var tool = new TodoTool();
        var setStatus = await tool.ExecuteAsync(
            Args("""{"action":"set_status","task":1,"done":true}"""), Ctx(), CancellationToken.None);

        Assert.IsFalse(setStatus.IsError, setStatus.Content);
        StringAssert.Contains(await File.ReadAllTextAsync(TodoPath), "- [x] fix crash");
    }
}
