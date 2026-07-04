using System.Text.Json;
using Dotsy.Cli;
using Dotsy.Core.Loop.Data;
using Dotsy.Core.Tools;

namespace Dotsy.Cli.Tests;

[TestClass]
public sealed class HeadlessStreamJsonTests
{
    [TestMethod]
    public void Format_IncludesRuntimeTypeName()
    {
        var doc = Parse(new TextChunk("hello"));
        Assert.AreEqual("TextChunk", doc.RootElement.GetProperty("type").GetString());
    }

    [TestMethod]
    public void Format_SerializesDerivedProperties_NotEmptyData()
    {
        // Regression: serializing against the abstract LoopEvent base produced "data":{}.
        var data = Parse(new TextChunk("hello")).RootElement.GetProperty("data");

        Assert.AreEqual(JsonValueKind.Object, data.ValueKind);
        Assert.AreEqual("hello", data.GetProperty("Text").GetString());
    }

    [TestMethod]
    public void Format_PreservesEnumAndNullableFields()
    {
        var data = Parse(new LoopEnded(EndReason.Error, "boom")).RootElement.GetProperty("data");

        Assert.AreEqual(nameof(EndReason.Error), data.GetProperty("Reason").GetString());
        Assert.AreEqual("boom", data.GetProperty("Message").GetString());
    }

    [TestMethod]
    public void Format_SerializesNestedRecordPayload()
    {
        var data = Parse(new ToolFinished(0, "Read", new ToolResult("ok", false)))
            .RootElement.GetProperty("data");

        Assert.AreEqual("Read", data.GetProperty("Name").GetString());
        Assert.AreEqual("ok", data.GetProperty("Result").GetProperty("Content").GetString());
    }

    [TestMethod]
    public void Format_PermissionRequired_DoesNotThrowOrBlockOnNonSerializableMembers()
    {
        var ev = new PermissionRequired(
            new DummyTool(),
            "Shell",
            "git status",
            new TaskCompletionSource<PermissionDecision>());

        // Tool (an interface) and Decision (a TaskCompletionSource whose .Task.Result
        // getter would block) are [JsonIgnore]; the useful fields still serialize.
        var data = Parse(ev).RootElement.GetProperty("data");

        Assert.AreEqual("Shell", data.GetProperty("ToolName").GetString());
        Assert.AreEqual("git status", data.GetProperty("DisplayArgument").GetString());
        Assert.IsFalse(data.TryGetProperty("Tool", out _));
        Assert.IsFalse(data.TryGetProperty("Decision", out _));
    }

    private static JsonDocument Parse(LoopEvent ev) => JsonDocument.Parse(HeadlessStreamJson.Format(ev));

    private sealed class DummyTool : Dotsy.Core.Tools.Interfaces.ITool
    {
        public string Name => "Shell";
        public string Description => "dummy";
        public JsonElement InputSchema => JsonDocument.Parse("{}").RootElement;
        public ToolSafety Safety => ToolSafety.Destructive;
        public bool IsCompletionSignal => false;
        public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext ctx, CancellationToken ct) =>
            Task.FromResult(ToolResult.Ok(""));
    }
}
