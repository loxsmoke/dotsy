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

    private ToolContext Ctx() => new()
    {
        Cwd = _tmpDir,
        LoopContext = new LoopContext()
    };

    private static JsonElement Args(string pattern, string path) =>
        JsonDocument.Parse($"{{\"pattern\":\"{pattern}\",\"path\":\"{path.Replace("\\", "\\\\")}\"}}").RootElement;

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
}
