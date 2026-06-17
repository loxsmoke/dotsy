using System.Text.Json;
using Dotsy.Core.Loop;
using Dotsy.Core.Tools;

namespace Dotsy.Core.Tests;

[TestClass]
public sealed class GrepToolTests
{
    private string _tmpDir = "";

    [TestInitialize]
    public void Setup()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"dotsy_grep_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
    }

    [TestCleanup]
    public void Teardown()
    {
        if (Directory.Exists(_tmpDir))
            Directory.Delete(_tmpDir, recursive: true);
    }

    // Auto-approve the "download ripgrep?" prompt so the tool can provision rg if it isn't
    // already installed/cached (mirrors a user clicking allow).
    private ToolContext Ctx() => new()
    {
        Cwd = _tmpDir,
        LoopContext = new LoopContext(),
        EmitEvent = ev =>
        {
            if (ev is PermissionRequired pr)
                pr.Decision.TrySetResult(PermissionDecision.AllowOnce);
            return Task.CompletedTask;
        }
    };

    private static JsonElement Args(string pattern, string path) =>
        JsonDocument.Parse($"{{\"pattern\":\"{pattern}\",\"path\":\"{path.Replace("\\", "\\\\")}\"}}").RootElement;

    private static JsonElement ArgsRaw(string json) => JsonDocument.Parse(json).RootElement;

    [TestMethod]
    public async Task OutputCappedAtMaxLinesWithTruncationNotice()
    {
        // Create 21 files * 100 matching lines = 2100 total > MaxLines(2000).
        // rg --max-count=100 caps per-file, so each file outputs exactly 100 lines.
        for (int f = 0; f < 21; f++)
        {
            var lines = Enumerable.Repeat("GREPMATCH", 100);
            await File.WriteAllLinesAsync(Path.Combine(_tmpDir, $"file{f:00}.txt"), lines);
        }

        var tool = new GrepTool();
        var result = await tool.ExecuteAsync(Args("GREPMATCH", _tmpDir), Ctx(), CancellationToken.None);

        Assert.IsFalse(result.IsError, $"Unexpected error: {result.Content}");
        StringAssert.Contains(result.Content, "<truncated:",
            "Expected truncation notice when output exceeds MaxLines");
    }

    [TestMethod]
    public async Task NoMatches_ReturnsNoMatchesFound()
    {
        await File.WriteAllTextAsync(Path.Combine(_tmpDir, "empty.txt"), "nothing here");

        var tool = new GrepTool();
        var result = await tool.ExecuteAsync(Args("XYZNOTPRESENT", _tmpDir), Ctx(), CancellationToken.None);

        Assert.IsFalse(result.IsError);
        StringAssert.Contains(result.Content, "No matches");
    }

    [TestMethod]
    public async Task GlobPassedAsPath_DoesNotError_AndFiltersByExtension()
    {
        await File.WriteAllTextAsync(Path.Combine(_tmpDir, "foo.cs"),  "NEEDLE here");
        await File.WriteAllTextAsync(Path.Combine(_tmpDir, "bar.txt"), "NEEDLE here");

        var tool = new GrepTool();
        // A model passing "**/*.cs" as the path used to blow up with os error 123 on Windows.
        var result = await tool.ExecuteAsync(ArgsRaw("""{"pattern":"NEEDLE","path":"**/*.cs"}"""), Ctx(), CancellationToken.None);

        Assert.IsFalse(result.IsError, $"Unexpected error: {result.Content}");
        StringAssert.Contains(result.Content, "foo.cs");
        Assert.IsFalse(result.Content.Contains("bar.txt"), "Glob should exclude non-.cs files");
    }

    [TestMethod]
    public async Task GlobParameter_FiltersFiles()
    {
        await File.WriteAllTextAsync(Path.Combine(_tmpDir, "foo.cs"),  "NEEDLE here");
        await File.WriteAllTextAsync(Path.Combine(_tmpDir, "bar.txt"), "NEEDLE here");

        var tool = new GrepTool();
        var result = await tool.ExecuteAsync(
            ArgsRaw($$"""{"pattern":"NEEDLE","path":"{{_tmpDir.Replace("\\", "\\\\")}}","glob":"*.cs"}"""),
            Ctx(), CancellationToken.None);

        Assert.IsFalse(result.IsError, $"Unexpected error: {result.Content}");
        StringAssert.Contains(result.Content, "foo.cs");
        Assert.IsFalse(result.Content.Contains("bar.txt"), "Glob filter should exclude non-.cs files");
    }

    [TestMethod]
    public async Task ExcludeParameter_SkipsMatchingDirectory()
    {
        var sub = Directory.CreateDirectory(Path.Combine(_tmpDir, "skipme")).FullName;
        await File.WriteAllTextAsync(Path.Combine(_tmpDir, "keep.cs"), "NEEDLE here");
        await File.WriteAllTextAsync(Path.Combine(sub, "drop.cs"),     "NEEDLE here");

        var tool = new GrepTool();
        var result = await tool.ExecuteAsync(
            ArgsRaw($$"""{"pattern":"NEEDLE","path":"{{_tmpDir.Replace("\\", "\\\\")}}","exclude":"skipme"}"""),
            Ctx(), CancellationToken.None);

        Assert.IsFalse(result.IsError, $"Unexpected error: {result.Content}");
        StringAssert.Contains(result.Content, "keep.cs");
        Assert.IsFalse(result.Content.Contains("drop.cs"), "Excluded directory should be skipped");
    }

    [TestMethod]
    public async Task NonexistentPath_ReturnsActionableError_NotRawRgError()
    {
        var tool = new GrepTool();
        // The terminal.gui repro: a literal path that doesn't exist (rg would emit "os error 2").
        var result = await tool.ExecuteAsync(
            ArgsRaw("""{"pattern":"NEEDLE","path":"terminal.gui"}"""), Ctx(), CancellationToken.None);

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.Content, "Search path not found");
        StringAssert.Contains(result.Content, "exclude");
        Assert.IsFalse(result.Content.Contains("os error"), "Should not leak the raw ripgrep error");
    }
}
