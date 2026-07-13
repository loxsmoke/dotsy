using Dotsy.Core.Loop;

namespace Dotsy.Core.Tests;

[TestClass]
public sealed class ToolCallSignatureTests
{
    private string _tmpDir = "";

    [TestInitialize]
    public void Setup()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"dotsy_sig_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
    }

    [TestCleanup]
    public void Teardown()
    {
        if (Directory.Exists(_tmpDir))
            Directory.Delete(_tmpDir, recursive: true);
    }

    // The loop guard's Read signatures fold in the file's on-disk state so that re-reading a file
    // that changed (e.g. after the agent's own edit, as demanded by the read-before-edit guard)
    // is never treated as a repeated call.

    [TestMethod]
    public void ReadSignature_ChangesWhenFileChanges()
    {
        var file = Path.Combine(_tmpDir, "a.cs");
        File.WriteAllText(file, "one");
        var args = $"{{\"path\":\"{file.Replace("\\", "\\\\")}\"}}";

        var before = AgentLoopHeuristics.ToolCallSignature(_tmpDir, "Read", args);
        File.WriteAllText(file, "two — different size");
        var after = AgentLoopHeuristics.ToolCallSignature(_tmpDir, "Read", args);

        StringAssert.Contains(before, "@");
        Assert.AreNotEqual(before, after);
    }

    [TestMethod]
    public void ReadSignature_StableWhileFileUnchanged()
    {
        var file = Path.Combine(_tmpDir, "a.cs");
        File.WriteAllText(file, "one");
        var args = $"{{\"path\":\"{file.Replace("\\", "\\\\")}\"}}";

        Assert.AreEqual(
            AgentLoopHeuristics.ToolCallSignature(_tmpDir, "Read", args),
            AgentLoopHeuristics.ToolCallSignature(_tmpDir, "Read", args));
    }

    [TestMethod]
    public void ReadSignature_RelativePath_ResolvesAgainstCwd()
    {
        File.WriteAllText(Path.Combine(_tmpDir, "a.cs"), "one");

        var signature = AgentLoopHeuristics.ToolCallSignature(_tmpDir, "Read", """{"path":"a.cs"}""");

        StringAssert.Contains(signature, "@");
    }

    [TestMethod]
    public void ReadSignature_MissingFile_HasNoStateSuffix()
    {
        var signature = AgentLoopHeuristics.ToolCallSignature(
            _tmpDir, "Read", """{"path":"missing.cs"}""");

        Assert.AreEqual("Read:{\"path\":\"missing.cs\"}", signature);
    }

    [TestMethod]
    public void NonReadTools_UsePlainSignature()
    {
        var file = Path.Combine(_tmpDir, "a.cs");
        File.WriteAllText(file, "one");
        var args = $"{{\"path\":\"{file.Replace("\\", "\\\\")}\"}}";

        var signature = AgentLoopHeuristics.ToolCallSignature(_tmpDir, "Grep", args);

        Assert.AreEqual($"Grep:{args}", signature);
    }

    [TestMethod]
    public void InvalidArgs_FallBackToPlainSignature()
    {
        Assert.AreEqual("Read:not json", AgentLoopHeuristics.ToolCallSignature(_tmpDir, "Read", "not json"));
        Assert.AreEqual("Read:{}", AgentLoopHeuristics.ToolCallSignature(_tmpDir, "Read", "{}"));
    }
}
