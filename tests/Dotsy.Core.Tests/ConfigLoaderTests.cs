using Dotsy.Core.Config;
using Dotsy.Core.Tools;

namespace Dotsy.Core.Tests;

[TestClass]
public sealed class ConfigLoaderTests
{
    [TestMethod]
    public async Task Load_ParsesModelMaxOutputTokensPerRequest()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"dotsy_config_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tmp, ".dotsy"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tmp, ".dotsy", "config.toml"), """
                [model]
                max_output_tokens_per_request = 2048
                """);

            var config = ConfigLoader.Load(tmp);

            Assert.AreEqual(2048, config.Model.MaxOutputTokensPerRequest);
        }
        finally
        {
            if (Directory.Exists(tmp))
                Directory.Delete(tmp, recursive: true);
        }
    }

    [TestMethod]
    public void Load_AppliesModelMaxOutputTokensPerRequestEnvOverride()
    {
        var oldValue = Environment.GetEnvironmentVariable("DOTSY_MODEL_MAX_OUTPUT_TOKENS_PER_REQUEST");
        try
        {
            Environment.SetEnvironmentVariable("DOTSY_MODEL_MAX_OUTPUT_TOKENS_PER_REQUEST", "4096");

            var config = ConfigLoader.Load(Path.GetTempPath());

            Assert.AreEqual(4096, config.Model.MaxOutputTokensPerRequest);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTSY_MODEL_MAX_OUTPUT_TOKENS_PER_REQUEST", oldValue);
        }
    }

    [TestMethod]
    public void Load_TrajectoryDefaultsDisabled()
    {
        var config = ConfigLoader.Load(Path.GetTempPath());

        Assert.IsFalse(config.Trajectory.Enabled);
        Assert.AreEqual(".dotsy/trajectories", config.Trajectory.Dir);
    }

    [TestMethod]
    public async Task Load_ParsesTrajectoryConfig()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"dotsy_config_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tmp, ".dotsy"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tmp, ".dotsy", "config.toml"), """
                [trajectory]
                enabled = true
                dir = "exports"
                """);

            var config = ConfigLoader.Load(tmp);

            Assert.IsTrue(config.Trajectory.Enabled);
            Assert.AreEqual("exports", config.Trajectory.Dir);
        }
        finally
        {
            if (Directory.Exists(tmp))
                Directory.Delete(tmp, recursive: true);
        }
    }

    [TestMethod]
    public void Load_AppliesTrajectoryEnvOverrides()
    {
        var oldEnabled = Environment.GetEnvironmentVariable("DOTSY_TRAJECTORY_ENABLED");
        var oldDir = Environment.GetEnvironmentVariable("DOTSY_TRAJECTORY_DIR");
        try
        {
            Environment.SetEnvironmentVariable("DOTSY_TRAJECTORY_ENABLED", "true");
            Environment.SetEnvironmentVariable("DOTSY_TRAJECTORY_DIR", "env-trajectories");

            var config = ConfigLoader.Load(Path.GetTempPath());

            Assert.IsTrue(config.Trajectory.Enabled);
            Assert.AreEqual("env-trajectories", config.Trajectory.Dir);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTSY_TRAJECTORY_ENABLED", oldEnabled);
            Environment.SetEnvironmentVariable("DOTSY_TRAJECTORY_DIR", oldDir);
        }
    }

    [TestMethod]
    public void ConfigEditor_IncludesModelAndTrajectoryKeys()
    {
        var keys = ConfigEditor.ParamList.SelectMany(g => g.Params).Select(p => p.Key).ToList();
        CollectionAssert.Contains(keys, "model.max_output_tokens_per_request");
        CollectionAssert.Contains(keys, "tui.left_panel_width_percentage");
        CollectionAssert.Contains(keys, "trajectory.enabled");
        CollectionAssert.Contains(keys, "trajectory.dir");

        var sections = ConfigEditor.GetSections(new DotsyConfig());
        Assert.IsTrue(sections.Any(s => s.Header == "model"
            && s.Kvs.Any(kv => kv.Key == "max_output_tokens_per_request")));
        Assert.IsTrue(sections.Any(s => s.Header == "trajectory"
            && s.Kvs.Any(kv => kv.Key == "enabled")
            && s.Kvs.Any(kv => kv.Key == "dir")));
        Assert.IsTrue(sections.Any(s => s.Header == "tui"
            && s.Kvs.Any(kv => kv.Key == "left_panel_width_percentage")));
    }

    [TestMethod]
    public async Task Load_ParsesTuiTheme()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"dotsy_config_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tmp, ".dotsy"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tmp, ".dotsy", "config.toml"), """
                [tui]
                theme = "borland"
                """);

            var config = ConfigLoader.Load(tmp);

            Assert.AreEqual("borland", config.Tui.Theme);
        }
        finally
        {
            if (Directory.Exists(tmp))
                Directory.Delete(tmp, recursive: true);
        }
    }

    [TestMethod]
    public void ConfigEditor_IncludesTuiThemeKeyAndSection()
    {
        var keys = ConfigEditor.ParamList.SelectMany(g => g.Params).Select(p => p.Key).ToList();
        CollectionAssert.Contains(keys, "tui.theme");

        var sections = ConfigEditor.GetSections(new DotsyConfig());
        Assert.IsTrue(sections.Any(s => s.Header == "tui"
            && s.Kvs.Any(kv => kv.Key == "theme" && kv.Value == "dark")));
    }

    [TestMethod]
    public async Task Load_ParsesTuiLeftPanelWidthPercentage()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"dotsy_config_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tmp, ".dotsy"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tmp, ".dotsy", "config.toml"), """
                [tui]
                left_panel_width_percentage = 64
                """);

            var config = ConfigLoader.Load(tmp);

            Assert.AreEqual(64, config.Tui.LeftPanelWidthPercentage);
        }
        finally
        {
            if (Directory.Exists(tmp))
                Directory.Delete(tmp, recursive: true);
        }
    }

    [TestMethod]
    public async Task Load_ParsesMcpServers()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"dotsy_config_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tmp, ".dotsy"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tmp, ".dotsy", "config.toml"), """
                [[mcp.servers]]
                name = "filesystem"
                command = "npx"
                args = ["-y", "@modelcontextprotocol/server-filesystem", "."]

                [[mcp.servers]]
                name = "remote"
                url = "http://localhost:3000/mcp"
                """);

            var config = ConfigLoader.Load(tmp);

            var stdio = config.Mcp.Servers.Single(s => s.Name == "filesystem");
            Assert.AreEqual(McpTransport.Stdio, stdio.Transport);
            Assert.AreEqual("npx", stdio.Command);
            CollectionAssert.AreEqual(
                new[] { "-y", "@modelcontextprotocol/server-filesystem", "." },
                stdio.Args);

            var http = config.Mcp.Servers.Single(s => s.Name == "remote");
            Assert.AreEqual(McpTransport.Http, http.Transport);
            Assert.AreEqual("http://localhost:3000/mcp", http.Url);
        }
        finally
        {
            if (Directory.Exists(tmp))
                Directory.Delete(tmp, recursive: true);
        }
    }

    [TestMethod]
    public async Task GetConfigSources_ReportsProjectFileForNonSecretKeysAndDefaultForUnset()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"dotsy_config_{Guid.NewGuid():N}");
        var projectConfig = Path.Combine(tmp, ".dotsy", "config.toml");
        Directory.CreateDirectory(Path.Combine(tmp, ".dotsy"));
        try
        {
            await File.WriteAllTextAsync(projectConfig, $"""
                [agent]
                max_turns = 99

                [{("model." + ProviderConfig.OpenAi)}]
                base_url = "http://proj"
                api_key = "proj-secret"
                """);

            var sources = ConfigLoader.GetConfigSources(tmp);

            Assert.AreEqual(projectConfig, sources.ProjectPath);
            Assert.IsTrue(sources.ProjectExists);
            Assert.AreEqual(projectConfig, sources.KeySources["agent.max_turns"]);
            Assert.AreEqual(projectConfig, sources.KeySources["model." + ProviderConfig.OpenAi + ".base_url"]);
            // A key not set in any file falls back to default.
            Assert.AreEqual("default", sources.KeySources["agent.nudge_limit"]);
            // API keys are never sourced from the project config, matching the loader's secrets rule.
            Assert.AreNotEqual(projectConfig, sources.KeySources["model." + ProviderConfig.OpenAi + ".api_key"]);
        }
        finally
        {
            if (Directory.Exists(tmp))
                Directory.Delete(tmp, recursive: true);
        }
    }

    [TestMethod]
    public void GetConfigSources_ReportsEnvVarOverride()
    {
        var old = Environment.GetEnvironmentVariable("DOTSY_AGENT_NUDGE_LIMIT");
        try
        {
            Environment.SetEnvironmentVariable("DOTSY_AGENT_NUDGE_LIMIT", "7");

            var sources = ConfigLoader.GetConfigSources(Path.GetTempPath());

            Assert.AreEqual("env:DOTSY_AGENT_NUDGE_LIMIT", sources.KeySources["agent.nudge_limit"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTSY_AGENT_NUDGE_LIMIT", old);
        }
    }

    [TestMethod]
    public void Gemini_ApiKeyResolvesFromEnvAndCatalogIncludesKeys()
    {
        var old = Environment.GetEnvironmentVariable(ProviderConfig.GeminiEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(ProviderConfig.GeminiEnvVar, "g-secret");

            var config = ConfigLoader.Load(Path.GetTempPath());
            config.Model.Provider = ProviderConfig.Gemini;
            config.Model.Gemini.Id = "gemini-2.5-flash-lite";

            Assert.AreEqual("g-secret", config.Model.Gemini.ApiKey);
            Assert.AreEqual("gemini-2.5-flash-lite", config.Model.ActiveModel.Id);
            Assert.AreEqual("Gemini", ProviderConfig.GetProviderDisplayName(ProviderConfig.Gemini));
            Assert.AreEqual($"env {ProviderConfig.GeminiEnvVar}", ConfigLoader.GetApiKeySource(config));

            var keys = ConfigEditor.ParamList.SelectMany(g => g.Params).Select(p => p.Key).ToList();
            CollectionAssert.Contains(keys, "model." + ProviderConfig.Gemini + ".id");
            CollectionAssert.Contains(keys, "model." + ProviderConfig.Gemini + ".api_key");
        }
        finally
        {
            Environment.SetEnvironmentVariable(ProviderConfig.GeminiEnvVar, old);
        }
    }

    [TestMethod]
    public void GetConfigSources_ReportsKnownSecretEnvVarForApiKey()
    {
        var old = Environment.GetEnvironmentVariable(ProviderConfig.AnthropicEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(ProviderConfig.AnthropicEnvVar, "from-env");

            var sources = ConfigLoader.GetConfigSources(Path.GetTempPath());

            Assert.AreEqual(
                $"env:{ProviderConfig.AnthropicEnvVar}",
                sources.KeySources["model." + ProviderConfig.Anthropic + ".api_key"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ProviderConfig.AnthropicEnvVar, old);
        }
    }

    [TestMethod]
    public void ToolRegistry_UnregisterRemovesTool()
    {
        var registry = new ToolRegistry();
        registry.Register(new DoneTool());

        Assert.IsTrue(registry.TryGetTool("Done", out _));
        Assert.IsTrue(registry.Unregister("Done"));
        Assert.IsFalse(registry.TryGetTool("Done", out _));
    }
}
