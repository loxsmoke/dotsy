using Dotsy.Core.Config;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Dotsy.Core.Tests;

[TestClass]
public sealed class ConfigEditorTests
{
    private string? _tmpDir;
    private string? _projectConfigFile;

    [TestInitialize]
    public void Setup()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"dotsy_edit_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
        _projectConfigFile = Path.Combine(_tmpDir, "config.toml");
        File.WriteAllText(_projectConfigFile, "");
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tmpDir))
            Directory.Delete(_tmpDir, recursive: true);
    }

    [TestMethod]
    public void Set_StandardInt_UpdatesConfigAndFile()
    {
        var cfg = new DotsyConfig();
        var key = "agent.max_turns";
        var value = "10";

        var (ok, msg) = ConfigEditor.Set(cfg, key, value, _projectConfigFile);

        Assert.IsTrue(ok, $"Set failed: {msg}");
        Assert.AreEqual(10, cfg.Agent.MaxTurns);
        
        var fileContent = File.ReadAllText(_projectConfigFile!);
        Assert.IsTrue(fileContent.Contains("max_turns = 10"));
    }

    [TestMethod]
    public void Set_KPostfix_UpdatesConfigAndFile()
    {
        var cfg = new DotsyConfig();
        var key = "model.max_output_tokens_per_request";
        var value = "64k"; // 64 * 1024 = 65536

        var (ok, msg) = ConfigEditor.Set(cfg, key, value, _projectConfigFile);

        Assert.IsTrue(ok, $"Set failed: {msg}");
        Assert.AreEqual(65536, cfg.Model.MaxOutputTokensPerRequest);
        
        var fileContent = File.ReadAllText(_projectConfigFile!);
        Assert.IsTrue(fileContent.Contains("max_output_tokens_per_request = 65536"));
    }

    [TestMethod]
    public void Set_MPostfix_UpdatesConfigAndFile()
    {
        var cfg = new DotsyConfig();
        var key = "model." + ProviderConfig.Ollama + ".max_context_tokens";
        var value = "2m"; // 2 * 1024 * 1024 = 2097152

        var (ok, msg) = ConfigEditor.Set(cfg, key, value, _projectConfigFile);

        Assert.IsTrue(ok, $"Set failed: {msg}");
        Assert.AreEqual(2097152, cfg.Model.Ollama.MaxContextTokens);
        
        var fileContent = File.ReadAllText(_projectConfigFile!);
        Assert.IsTrue(fileContent.Contains("max_context_tokens = 2097152"));
    }

    [TestMethod]
    public void Set_InvalidNumeric_ReturnsError()
    {
        var cfg = new DotsyConfig();
        var key = "agent.max_turns";
        var value = "64x"; // Invalid postfix

        var (ok, msg) = ConfigEditor.Set(cfg, key, value, _projectConfigFile);

        Assert.IsFalse(ok, "Set should have failed for invalid numeric format");
        Assert.IsTrue(msg.Contains("cannot convert"));
    }

    [TestMethod]
    public void Set_InvalidValueForType_ReturnsError()
    {
        var cfg = new DotsyConfig();
        var key = "agent.max_turns";
        var value = "not-a-number";

        var (ok, msg) = ConfigEditor.Set(cfg, key, value, _projectConfigFile);

        Assert.IsFalse(ok, "Set should have failed for non-numeric value on int property");
    }

    [TestMethod]
    public void GetValueHint_KnownValidValues_IncludesValues()
    {
        var param = ConfigEditor.FindParam("tui.theme");

        Assert.IsNotNull(param);
        Assert.AreEqual(
            "color theme (valid: dark | light | system | borland)",
            ConfigEditor.GetValueHint(param));
    }

    [TestMethod]
    public void GetValueHint_NoKnownRange_DoesNotShowRange()
    {
        var param = ConfigEditor.FindParam("model.max_output_tokens_per_request");

        Assert.IsNotNull(param);
        Assert.AreEqual(
            "max output tokens per request",
            ConfigEditor.GetValueHint(param));
        Assert.IsFalse(ConfigEditor.GetValueHint(param).Contains("range:", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void ParamList_AllKeysResolveToWritableConfigProperties()
    {
        var cfg = new DotsyConfig();

        foreach (var group in ConfigEditor.ParamList)
        {
            foreach (var param in group.Params)
            {
                var dummyValue = param.Type switch
                {
                    "int"   => "1",
                    "float" => "0.5",
                    "bool"  => "true",
                    _       => "test"
                };

                var (ok, _, _, msg) = ConfigEditor.CheckUnchanged(cfg, param.Key, dummyValue);
                Assert.IsTrue(ok,
                    $"ParamList key '{param.Key}' (type '{param.Type}') did not resolve to a writable config property: {msg}");
            }
        }
    }

    [TestMethod]
    public void GetSections_KeysMatchParamList()
    {
        var paramKeys = ConfigEditor.ParamList
            .SelectMany(group => group.Params.Select(param => param.Key))
            .OrderBy(key => key)
            .ToArray();
        var sectionKeys = ConfigEditor.GetSections(new DotsyConfig())
            .SelectMany(section => section.Kvs.Select(kv => $"{section.Header}.{kv.Key}"))
            .OrderBy(key => key)
            .ToArray();

        CollectionAssert.AreEqual(paramKeys, sectionKeys);
    }

    [TestMethod]
    public void Catalog_IncludesLoopGuardAndAzureApiVersion()
    {
        var cfg = new DotsyConfig();
        cfg.Agent.RepeatWindowTurns = 12;
        cfg.Agent.RepeatThreshold = 4;
        cfg.Model.Azure.ApiVersion = "test-version";

        Assert.IsNotNull(ConfigEditor.FindParam("agent.repeat_window_turns"));
        Assert.IsNotNull(ConfigEditor.FindParam("agent.repeat_threshold"));
        Assert.IsNotNull(ConfigEditor.FindParam("model.azure.api_version"));

        var sections = ConfigEditor.GetSections(cfg);
        Assert.AreEqual("12", FindValue(sections, "agent", "repeat_window_turns"));
        Assert.AreEqual("4", FindValue(sections, "agent", "repeat_threshold"));
        Assert.AreEqual("test-version", FindValue(sections, "model.azure", "api_version"));
    }

    private static string FindValue(
        IEnumerable<ConfigEditor.Section> sections,
        string sectionName,
        string key) =>
        sections.Single(section => section.Header == sectionName)
            .Kvs.Single(kv => kv.Key == key)
            .Value;
}
