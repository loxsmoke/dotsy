using Dotsy.Core.Loop;
using Dotsy.Core.Loop.Data;

namespace Dotsy.Core.Tests;

[TestClass]
public sealed class StaleBuildHeuristicTests
{
    // Real outputs from the webcam-sec dogfooding sessions that motivated the heuristic: one
    // missing NuGet package surfaced as a sequence of unrelated-looking errors across 46 builds.
    private const string DuplicateXClass =
        "C:\\proj\\App.axaml : Avalonia error AVLN2002: Duplicate x:Class directive [C:\\proj\\P.csproj]\nBuild FAILED.";
    private const string MissingAssembly =
        "C:\\proj\\App.axaml(6,23): Avalonia error AVLN2000: Assembly \"Avalonia.Themes.Fluent\" was not found\nBuild FAILED.";
    private const string CsError =
        "C:\\proj\\Program.cs(52,18): error CS1519: Invalid token '=' in a member declaration\nBuild FAILED.";
    private const string PassingBuild =
        "Build succeeded.\n    0 Warning(s)\n    0 Error(s)";

    // ── signature extraction ──────────────────────────────────────────────────

    [TestMethod]
    public void Signature_ExtractsAndDedupesDiagnosticCodes()
    {
        Assert.AreEqual("AVLN2002", AgentLoopHeuristics.BuildErrorSignature(DuplicateXClass));
        Assert.AreEqual("CS1519", AgentLoopHeuristics.BuildErrorSignature(CsError + "\n" + CsError));
        Assert.AreEqual(
            "AVLN2000+CS1519",
            AgentLoopHeuristics.BuildErrorSignature(MissingAssembly + "\n" + CsError));
    }

    [TestMethod]
    public void Signature_IgnoresNuGetNoiseWhenRealErrorsPresent()
    {
        var output = "warning NU1903: Package has a known vulnerability\n" + DuplicateXClass;
        Assert.AreEqual("AVLN2002", AgentLoopHeuristics.BuildErrorSignature(output));
    }

    [TestMethod]
    public void Signature_FallsBackToFirstErrorLine()
    {
        var output = "Determining projects to restore...\nerror : Ambiguous project name 'WebCamSec'.\nBuild FAILED.";
        Assert.AreEqual("error : Ambiguous project name 'WebCamSec'.", AgentLoopHeuristics.BuildErrorSignature(output));
    }

    // ── hint triggering ───────────────────────────────────────────────────────

    [TestMethod]
    public void ShiftingErrors_TriggerHintOnThirdFailure_Once()
    {
        var ctx = new LoopContext();

        Assert.IsNull(AgentLoopHeuristics.ObserveBuildOutcome(ctx, failed: true, DuplicateXClass));
        Assert.IsNull(AgentLoopHeuristics.ObserveBuildOutcome(ctx, failed: true, MissingAssembly));
        var hint = AgentLoopHeuristics.ObserveBuildOutcome(ctx, failed: true, CsError);

        Assert.IsNotNull(hint);
        StringAssert.Contains(hint, "obj and bin");
        StringAssert.Contains(hint, "AVLN2002");
        StringAssert.Contains(hint, "AVLN2000");

        // Same episode: the hint fires only once, further failures stay silent.
        Assert.IsNull(AgentLoopHeuristics.ObserveBuildOutcome(ctx, failed: true, DuplicateXClass));
    }

    [TestMethod]
    public void SameErrorRepeating_NeverTriggersHint()
    {
        // The model failing to fix one stable error is a different problem — no clean-rebuild hint.
        var ctx = new LoopContext();
        for (var i = 0; i < 6; i++)
            Assert.IsNull(AgentLoopHeuristics.ObserveBuildOutcome(ctx, failed: true, CsError));
    }

    [TestMethod]
    public void PassingBuild_ResetsAndReArms()
    {
        var ctx = new LoopContext();
        AgentLoopHeuristics.ObserveBuildOutcome(ctx, true, DuplicateXClass);
        AgentLoopHeuristics.ObserveBuildOutcome(ctx, true, MissingAssembly);
        Assert.IsNotNull(AgentLoopHeuristics.ObserveBuildOutcome(ctx, true, CsError));

        Assert.IsNull(AgentLoopHeuristics.ObserveBuildOutcome(ctx, failed: false, PassingBuild));
        Assert.AreEqual(0, ctx.ConsecutiveBuildFailures);
        Assert.AreEqual(0, ctx.BuildErrorSignatures.Count);

        // A fresh episode of shifting errors can trigger the hint again.
        AgentLoopHeuristics.ObserveBuildOutcome(ctx, true, DuplicateXClass);
        AgentLoopHeuristics.ObserveBuildOutcome(ctx, true, MissingAssembly);
        Assert.IsNotNull(AgentLoopHeuristics.ObserveBuildOutcome(ctx, true, CsError));
    }

    [TestMethod]
    public void TwoFailures_NotEnoughToTrigger()
    {
        var ctx = new LoopContext();
        Assert.IsNull(AgentLoopHeuristics.ObserveBuildOutcome(ctx, true, DuplicateXClass));
        Assert.IsNull(AgentLoopHeuristics.ObserveBuildOutcome(ctx, true, MissingAssembly));
    }
}
