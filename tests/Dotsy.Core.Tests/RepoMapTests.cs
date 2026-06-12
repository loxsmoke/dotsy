using Dotsy.Core.Retrieval;

namespace Dotsy.Core.Tests;

[TestClass]
public sealed class RepoMapTests
{
    [TestMethod]
    public void Build_ScalesBudget_WhenNoFilesAreExplicitlyMentioned()
    {
        var outlines = new[]
        {
            Outline("A.cs", new string('a', 80)),
            Outline("B.cs", new string('b', 80))
        };

        var withoutExplicitContext = RepoMap.Build(outlines, tokenBudget: 30, mentionedFiles: []);
        var withExplicitContext = RepoMap.Build(outlines, tokenBudget: 30, mentionedFiles: ["A.cs"]);

        StringAssert.Contains(withoutExplicitContext, "A.cs");
        StringAssert.Contains(withoutExplicitContext, "B.cs");
        StringAssert.Contains(withExplicitContext, "A.cs");
        Assert.IsFalse(withExplicitContext.Contains("B.cs", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Build_DownWeightsGeneratedFiles()
    {
        var outlines = new[]
        {
            Outline("Widget.g.cs", "  public class WidgetGenerated\n    public string Name\n"),
            Outline("WidgetService.cs", "  public class WidgetService\n    public void Run(...)\n")
        };

        var map = RepoMap.Build(outlines, tokenBudget: 20, mentionedFiles: ["unrelated.cs"]);

        StringAssert.Contains(map, "WidgetService.cs");
        Assert.IsFalse(map.Contains("Widget.g.cs", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Build_DownWeightsPropertyHeavyOutlines()
    {
        var outlines = new[]
        {
            Outline("WidgetModel.cs", """
                  public class WidgetModel
                    public string Name
                    public string Title
                    public int Count
                    public bool Enabled
                """),
            Outline("WidgetRunner.cs", """
                  public class WidgetRunner
                    public void Run(...)
                    public void Stop(...)
                """)
        };

        var map = RepoMap.Build(outlines, tokenBudget: 30, mentionedFiles: ["unrelated.cs"]);

        StringAssert.Contains(map, "WidgetRunner.cs");
        Assert.IsFalse(map.Contains("WidgetModel.cs", StringComparison.Ordinal));
    }

    private static FileOutline Outline(string fileName, string body) => new()
    {
        FilePath = fileName,
        Outline = $"// {fileName}\n{body}\n",
        ReferencedFiles = [],
        LastWrite = DateTimeOffset.UtcNow
    };
}
