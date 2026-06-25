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
        var key = "model.ollama.max_context_tokens";
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
}
