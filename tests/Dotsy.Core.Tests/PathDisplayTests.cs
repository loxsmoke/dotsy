using Dotsy.Core.Utils;

namespace Dotsy.Core.Tests;

[TestClass]
public sealed class PathDisplayTests
{
    [TestMethod]
    public void MakeRelative_ReturnsDot_ForEmptyPath()
    {
        Assert.AreEqual(".", PathDisplay.MakeRelative("", Path.GetTempPath()));
    }

    [TestMethod]
    public void MakeRelative_ReturnsDot_ForWorkingDirectory()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "dotsy-path-display");

        Assert.AreEqual(".", PathDisplay.MakeRelative(cwd, cwd));
    }

    [TestMethod]
    public void MakeRelative_ReturnsNestedPath_ForAbsolutePathInsideWorkingDirectory()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "dotsy-path-display");
        var path = Path.Combine(cwd, "src", "Program.cs");

        Assert.AreEqual(
            Path.Combine("src", "Program.cs"),
            PathDisplay.MakeRelative(path, cwd));
    }

    [TestMethod]
    public void MakeRelative_ResolvesRelativePathAgainstWorkingDirectory()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "dotsy-path-display");
        var path = Path.Combine("src", "Program.cs");

        Assert.AreEqual(path, PathDisplay.MakeRelative(path, cwd));
    }

    [TestMethod]
    public void MakeRelative_PreservesAbsolutePathOutsideWorkingDirectory()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "dotsy-path-display", "repo");
        var path = Path.Combine(Path.GetTempPath(), "dotsy-path-display", "other", "file.cs");

        Assert.AreEqual(path, PathDisplay.MakeRelative(path, cwd));
    }
}
