using Dotsy.Core.Loop.Data;
using Dotsy.Core.Retrieval;

namespace Dotsy.Core.Tests;

[TestClass]
public sealed class RepoMapTests
{
    [TestMethod]
    public void Build_ScalesBudget_WhenNoFilesAreExplicitlyMentioned()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "A.cs"),
                "public class A { public void Method1() { } public void Method2() { } }");
            File.WriteAllText(Path.Combine(dir, "B.cs"),
                "public class B { public void Method1() { } public void Method2() { } }");

            var withoutExplicitContext = RepoMap.Build(dir, tokenBudget: 30, ctx: new LoopContext());

            var ctxWithA = new LoopContext();
            ctxWithA.AddedFiles.Add("A.cs");
            var withExplicitContext = RepoMap.Build(dir, tokenBudget: 30, ctx: ctxWithA);

            Assert.IsNotNull(withoutExplicitContext);
            StringAssert.Contains(withoutExplicitContext, "A.cs");
            StringAssert.Contains(withoutExplicitContext, "B.cs");
            Assert.IsNotNull(withExplicitContext);
            StringAssert.Contains(withExplicitContext, "A.cs");
            Assert.IsFalse(withExplicitContext.Contains("B.cs", StringComparison.Ordinal));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [TestMethod]
    public void Build_DownWeightsGeneratedFiles()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "Widget.g.cs"),
                "public class WidgetGenerated { public string Name { get; set; } }");
            File.WriteAllText(Path.Combine(dir, "WidgetService.cs"),
                "public class WidgetService { public void Run() { } }");

            var ctx = new LoopContext();
            ctx.AddedFiles.Add("unrelated.cs");
            var map = RepoMap.Build(dir, tokenBudget: 20, ctx: ctx);

            Assert.IsNotNull(map);
            StringAssert.Contains(map, "WidgetService.cs");
            Assert.IsFalse(map.Contains("Widget.g.cs", StringComparison.Ordinal));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [TestMethod]
    public void Build_DownWeightsPropertyHeavyOutlines()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "WidgetModel.cs"), """
                public class WidgetModel
                {
                    public string Name { get; set; }
                    public string Title { get; set; }
                    public int Count { get; set; }
                    public bool Enabled { get; set; }
                }
                """);
            File.WriteAllText(Path.Combine(dir, "WidgetRunner.cs"), """
                public class WidgetRunner
                {
                    public void Run() { }
                    public void Stop() { }
                }
                """);

            var ctx = new LoopContext();
            ctx.AddedFiles.Add("unrelated.cs");
            var map = RepoMap.Build(dir, tokenBudget: 30, ctx: ctx);

            Assert.IsNotNull(map);
            StringAssert.Contains(map, "WidgetRunner.cs");
            Assert.IsFalse(map.Contains("WidgetModel.cs", StringComparison.Ordinal));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "RepoMapTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        return dir;
    }
}
