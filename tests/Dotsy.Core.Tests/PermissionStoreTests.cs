using Dotsy.Core.Config;
using Dotsy.Core.Loop;

namespace Dotsy.Core.Tests;

[TestClass]
public sealed class PermissionStoreTests
{
    private static PermissionStore Store(string[] allow, string[] deny) =>
        new(new PermissionsConfig { AlwaysAllow = [..allow], NeverAllow = [..deny] }, Path.GetTempPath());

    // ── Deny wins over allow ──────────────────────────────────────────────────

    [TestMethod]
    public void DenyRule_WinsOverAllowRule()
    {
        var store = Store(allow: ["Shell(dangerous)"], deny: ["Shell(dangerous)"]);

        var verdict = store.Evaluate("Shell", "dangerous");

        Assert.AreEqual(PermissionVerdict.Deny, verdict);
    }

    // ── Glob pattern matching ─────────────────────────────────────────────────

    [TestMethod]
    public void GlobPattern_AllowsMatchingWildcard()
    {
        var store = Store(allow: ["Shell(git *)"], deny: []);

        Assert.AreEqual(PermissionVerdict.Allow, store.Evaluate("Shell", "git status"));
        Assert.AreEqual(PermissionVerdict.Allow, store.Evaluate("Shell", "git log --oneline"));
    }

    [TestMethod]
    public void GlobPattern_DoesNotMatchUnrelatedTool()
    {
        var store = Store(allow: ["Shell(git *)"], deny: []);

        // "Shell(dotnet build)" does not match "Shell(git *)"
        var verdict = store.Evaluate("Shell", "dotnet build");

        Assert.AreNotEqual(PermissionVerdict.Allow, verdict);
    }

    [TestMethod]
    public void HardDenial_CannotBeOverridden()
    {
        // Even Yolo=false; hard denials are built in
        var store = Store(allow: ["Shell(rm -rf /)"], deny: []);

        Assert.AreEqual(PermissionVerdict.Deny, store.Evaluate("Shell", "rm -rf /"));
    }

    [TestMethod]
    public void Yolo_AlwaysReturnsAllow()
    {
        var store = Store(allow: [], deny: ["Shell(*)"]);
        store.Yolo = true;

        Assert.AreEqual(PermissionVerdict.Allow, store.Evaluate("Shell", "anything"));
    }
}
