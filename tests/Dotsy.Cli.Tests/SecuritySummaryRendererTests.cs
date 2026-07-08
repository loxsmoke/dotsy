using System.Text.Json;
using Dotsy.Cli.SlashCommands;
using Dotsy.Core.Config;
using Dotsy.Core.Loop;
using Dotsy.Core.Loop.Data;
using Dotsy.Core.Skills;
using Dotsy.Core.Tools;
using Dotsy.Core.Tools.Interfaces;

namespace Dotsy.Cli.Tests;

[TestClass]
public sealed class SecuritySummaryRendererTests
{
    private string _tmpDir = "";

    [TestInitialize]
    public void Setup()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"dotsy_sec_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
    }

    [TestCleanup]
    public void Teardown()
    {
        if (Directory.Exists(_tmpDir))
            Directory.Delete(_tmpDir, recursive: true);
    }

    [TestMethod]
    public void SlashCommandRegistry_IncludesSec()
    {
        var registry = SlashCommandRegistry.CreateDefault();
        Assert.IsTrue(registry.Names.Contains("sec", StringComparer.OrdinalIgnoreCase));
        Assert.IsTrue(registry.Usages.Any(u => u.Syntax == "/sec"));
    }

    [TestMethod]
    public void Render_ShowsRuleOrderingAndSources()
    {
        var config = new DotsyConfig
        {
            Permissions = new PermissionsConfig
            {
                AlwaysAllow = ["Shell(dotnet build *)"],
                NeverAllow = ["Shell(format *)"]
            }
        };
        var permissions = new PermissionStore(config.Permissions, _tmpDir);
        permissions.AllowForSession("Shell", "{\"command\":\"echo hi\"}");
        permissions.DenyForSession("Shell", "{\"command\":\"danger\"}");

        var summary = Render(config, permissions);

        StringAssert.Contains(summary, "Rule sources");
        var hardDeny = summary.IndexOf("deny  Shell rm -rf /", StringComparison.Ordinal);
        var configDeny = summary.IndexOf("deny  Shell format any", StringComparison.Ordinal);
        var configAllow = summary.IndexOf("allow Shell dotnet build any", StringComparison.Ordinal);
        Assert.IsTrue(hardDeny >= 0);
        Assert.IsTrue(configDeny > hardDeny);
        Assert.IsTrue(configAllow > configDeny);
        StringAssert.Contains(summary, "deny: Shell command danger");
        StringAssert.Contains(summary, "allow once: Shell command echo hi");
    }

    [TestMethod]
    public void Render_ShowsYoloAndHeadlessModes()
    {
        var config = new DotsyConfig();
        var permissions = new PermissionStore(config.Permissions, _tmpDir) { Yolo = true };

        var summary = SecuritySummaryRenderer.Render(new SecuritySummaryRequest(
            config,
            permissions,
            _tmpDir,
            MakeRegistry(),
            Headless: true));

        StringAssert.Contains(summary, "prompts: disabled");
        StringAssert.Contains(summary, "yolo: true");
        StringAssert.Contains(summary, "headless: true");
        StringAssert.Contains(summary, "Shell  allow");
    }

    [TestMethod]
    public void Render_ShowsProjectWriteApprovalDotsyAskAndOutsideNoAccess()
    {
        var config = new DotsyConfig { Permissions = new PermissionsConfig { AlwaysAllow = [], NeverAllow = [] } };
        var permissions = new PermissionStore(config.Permissions, _tmpDir);
        permissions.AllowWriteForProject();

        var summary = Render(config, permissions);

        StringAssert.Contains(summary, "project write approval: enabled for project except .dotsy");
        StringAssert.Contains(summary, $"{Path.Combine(_tmpDir, ".dotsy")}  write tools: ask");
        StringAssert.Contains(summary, $"outside {Path.GetFullPath(_tmpDir)}  write tools: no access");
        StringAssert.Contains(summary, "allow for project: Write, Edit, and MultiEdit inside the project except .dotsy");
    }

    [TestMethod]
    public void PermissionStore_EvaluatesJsonPathForOutsideCwdAndDotsy()
    {
        var config = new PermissionsConfig { AlwaysAllow = [], NeverAllow = [] };
        var permissions = new PermissionStore(config, _tmpDir);
        permissions.AllowWriteForProject();

        Assert.AreEqual(PermissionVerdict.Allow, permissions.Evaluate("Write", JsonPath(Path.Combine(_tmpDir, "ok.txt"))));
        Assert.AreEqual(PermissionVerdict.Ask, permissions.Evaluate("Write", JsonPath(Path.Combine(_tmpDir, ".dotsy", "permissions.json"))));
        Assert.AreEqual(PermissionVerdict.Deny, permissions.Evaluate("Write", JsonPath(Path.Combine(Path.GetTempPath(), "outside.txt"))));
    }

    [TestMethod]
    public void Render_ShowsAddedFilesAndInProgressCategories()
    {
        var config = new DotsyConfig { Permissions = new PermissionsConfig { AlwaysAllow = [], NeverAllow = [] } };
        var permissions = new PermissionStore(config.Permissions, _tmpDir);
        var ctx = new LoopContext("sec-session");
        ctx.AddedFiles.Add(Path.Combine(_tmpDir, "readme.md"));

        var summary = SecuritySummaryRenderer.Render(new SecuritySummaryRequest(
            config,
            permissions,
            _tmpDir,
            Registry: null,
            LoopContext: ctx));

        StringAssert.Contains(summary, "/add");
        StringAssert.Contains(summary, "registered tools: not yet detailed");
        StringAssert.Contains(summary, "in progress");
        StringAssert.Contains(summary, "/sec is display-only");
    }

    [TestMethod]
    public void Render_SummarizesLargeRawJsonRules()
    {
        var config = new DotsyConfig { Permissions = new PermissionsConfig { AlwaysAllow = [], NeverAllow = [] } };
        var permissions = new PermissionStore(config.Permissions, _tmpDir);
        var hugeContent = new string('x', 2_000);
        permissions.AllowForSession("Write", JsonSerializer.Serialize(new
        {
            path = Path.Combine(_tmpDir, "big.txt"),
            content = hugeContent
        }));

        var summary = Render(config, permissions);

        StringAssert.Contains(summary, "Write path ");
        Assert.IsFalse(summary.Contains(hugeContent));
        Assert.IsTrue(summary.Length < 8_000, $"summary was unexpectedly large: {summary.Length}");
    }

    [TestMethod]
    public void Render_UsesPlainAsciiWithoutRawRulePunctuation()
    {
        var config = new DotsyConfig
        {
            Permissions = new PermissionsConfig
            {
                AlwaysAllow = ["Shell(dotnet build *)"],
                NeverAllow = []
            }
        };
        var permissions = new PermissionStore(config.Permissions, _tmpDir);
        permissions.AllowForSession("Shell", JsonSerializer.Serialize(new
        {
            command = "echo \u2603"
        }));

        var summary = Render(config, permissions);

        Assert.IsTrue(summary.All(ch => ch is '\n' or (>= ' ' and <= '~')));
        Assert.IsFalse(summary.Contains('\r'));
        Assert.IsFalse(summary.Contains('{'));
        Assert.IsFalse(summary.Contains('}'));
        Assert.IsFalse(summary.Contains('<'));
        Assert.IsFalse(summary.Contains('>'));
        Assert.IsFalse(summary.Contains('"'));
        Assert.IsFalse(summary.Contains("always_allow"));
        Assert.IsFalse(summary.Contains("never_allow"));
    }

    private string Render(DotsyConfig config, PermissionStore permissions) =>
        SecuritySummaryRenderer.Render(new SecuritySummaryRequest(
            config,
            permissions,
            _tmpDir,
            MakeRegistry(),
            new LoopContext("sec-session")));

    private static ToolRegistry MakeRegistry()
    {
        var registry = new ToolRegistry();
        registry.Register(new ReadTool());
        registry.Register(new WriteTool());
        registry.Register(new EditTool());
        registry.Register(new ShellTool());
        registry.Register(new SkillTool(new SkillDiscovery(new SkillsConfig(), Directory.GetCurrentDirectory())));
        registry.Register(new TaskTool());
        registry.Register(new FakeMcpTool());
        return registry;
    }

    private static string JsonPath(string path) =>
        "{\"path\":\"" + path.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"}";

    private sealed class FakeMcpTool : ITool
    {
        public string Name => "ExternalSearch";
        public string Description => "External MCP search.";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new { type = "object" });
        public ToolSafety Safety => ToolSafety.Sequential;
        public bool IsCompletionSignal => false;
        public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext ctx, CancellationToken ct) =>
            Task.FromResult(ToolResult.Ok("ok"));
    }
}
