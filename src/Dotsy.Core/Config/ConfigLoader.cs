using System.Reflection;
using Tomlyn;
using Tomlyn.Model;

namespace Dotsy.Core.Config;

public static class ConfigLoader
{
    private static readonly string GlobalConfigPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "dotsy", "config.toml");

    public static DotsyConfig Load(string cwd)
    {
        var config = DefaultConfig.Create();
        ApplyToml(config, GlobalConfigPath, allowSecrets: true);

        var projectToml = FindProjectConfig(cwd);
        if (projectToml is not null)
            ApplyToml(config, projectToml, allowSecrets: false);

        ApplyEnvVars(config);
        return config;
    }

    public static string? FindProjectConfig(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, ".dotsy", "config.toml");
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private static void ApplyToml(DotsyConfig config, string path, bool allowSecrets)
    {
        if (!File.Exists(path))
            return;

        var text = File.ReadAllText(path);
        if (!Toml.TryToModel(text, out TomlTable? table, out _, path))
            return;

        if (table.TryGetValue("model", out var modelObj) && modelObj is TomlTable model)
        {
            ApplyScalars(config.Model, model);
            if (allowSecrets)
            {
                ApplyNestedSecrets(config.Model.Anthropic, model, "anthropic");
                ApplyNestedSecrets(config.Model.OpenAi, model, "openai");
                ApplyNestedSecrets(config.Model.Azure, model, "azure");
                ApplyNestedSecrets(config.Model.Compatible, model, "compatible");
            }
            if (model.TryGetValue("ollama", out var ollamaObj) && ollamaObj is TomlTable ollama)
                ApplyScalars(config.Model.Ollama, ollama);
            if (allowSecrets)
            {
                if (model.TryGetValue("openai", out var oaiObj) && oaiObj is TomlTable oai)
                    ApplyScalars(config.Model.OpenAi, oai);
                if (model.TryGetValue("azure", out var azObj) && azObj is TomlTable az)
                    ApplyScalars(config.Model.Azure, az);
            }
        }

        if (table.TryGetValue("agent", out var agentObj) && agentObj is TomlTable agent)
            ApplyScalars(config.Agent, agent);

        if (table.TryGetValue("compaction", out var compObj) && compObj is TomlTable comp)
            ApplyScalars(config.Compaction, comp);

        if (table.TryGetValue("retrieval", out var retObj) && retObj is TomlTable ret)
            ApplyScalars(config.Retrieval, ret);

        if (table.TryGetValue("skills", out var skillsObj) && skillsObj is TomlTable skills)
        {
            ApplyScalars(config.Skills, skills);
            if (skills.TryGetValue("paths", out var pathsObj) && pathsObj is TomlArray paths)
                config.Skills.Paths = [.. paths.OfType<string>()];
        }

        if (table.TryGetValue("mcp", out var mcpObj) && mcpObj is TomlTable mcp)
        {
            ApplyScalars(config.Mcp, mcp);
            if (mcp.TryGetValue("servers", out var serversObj))
                config.Mcp.Servers = ParseMcpServers(serversObj);
        }

        if (table.TryGetValue("git", out var gitObj) && gitObj is TomlTable git)
            ApplyScalars(config.Git, git);

        if (table.TryGetValue("tui", out var tuiObj) && tuiObj is TomlTable tui)
            ApplyScalars(config.Tui, tui);

        if (table.TryGetValue("permissions", out var permObj) && permObj is TomlTable perm)
        {
            if (perm.TryGetValue("always_allow", out var aa) && aa is TomlArray aaArr)
                config.Permissions.AlwaysAllow = [.. aaArr.OfType<string>()];
            if (perm.TryGetValue("never_allow", out var na) && na is TomlArray naArr)
                config.Permissions.NeverAllow = [.. naArr.OfType<string>()];
        }

        if (table.TryGetValue("session", out var sessObj) && sessObj is TomlTable sess)
            ApplyScalars(config.Session, sess);

        if (table.TryGetValue("trajectory", out var trajObj) && trajObj is TomlTable traj)
            ApplyScalars(config.Trajectory, traj);
    }

    private static void ApplyNestedSecrets(object target, TomlTable parent, string key)
    {
        if (parent.TryGetValue(key, out var nested) && nested is TomlTable table)
            ApplyScalars(target, table);
    }

    private static void ApplyScalars(object target, TomlTable table)
    {
        var props = target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && IsScalarType(p.PropertyType));

        foreach (var prop in props)
        {
            var tomlKey = ToConfigKey(prop.Name);
            if (!table.TryGetValue(tomlKey, out var raw)
                && (LegacyConfigKey(prop.Name) is not { } legacyKey || !table.TryGetValue(legacyKey, out raw)))
                continue;

            var converted = ConvertValue(raw, prop.PropertyType);
            if (converted is not null)
                prop.SetValue(target, converted);
        }
    }

    private static bool IsScalarType(Type t) =>
        t == typeof(string) || t == typeof(int) || t == typeof(float) ||
        t == typeof(double) || t == typeof(bool) || t == typeof(long);

    private static object? ConvertValue(object raw, Type targetType)
    {
        try
        {
            if (targetType == typeof(string)) return raw.ToString();
            if (targetType == typeof(int)) return Convert.ToInt32(raw);
            if (targetType == typeof(long)) return Convert.ToInt64(raw);
            if (targetType == typeof(float)) return Convert.ToSingle(raw);
            if (targetType == typeof(double)) return Convert.ToDouble(raw);
            if (targetType == typeof(bool)) return Convert.ToBoolean(raw);
        }
        catch { }
        return null;
    }

    private static string ToSnakeCase(string name)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0)
                sb.Append('_');
            sb.Append(char.ToLower(name[i]));
        }
        return sb.ToString();
    }

    private static void ApplyEnvVars(DotsyConfig config)
    {
        ApplyEnvSection("MODEL", config.Model);
        ApplyEnvSection("MODEL_ANTHROPIC", config.Model.Anthropic);
        ApplyEnvSection("MODEL_OPENAI", config.Model.OpenAi);
        ApplyEnvSection("MODEL_AZURE", config.Model.Azure);
        ApplyEnvSection("MODEL_OLLAMA", config.Model.Ollama);
        ApplyEnvSection("MODEL_COMPATIBLE", config.Model.Compatible);
        ApplyEnvSection("AGENT", config.Agent);
        ApplyEnvSection("COMPACTION", config.Compaction);
        ApplyEnvSection("RETRIEVAL", config.Retrieval);
        ApplyEnvSection("SKILLS", config.Skills);
        ApplyEnvSection("MCP", config.Mcp);
        ApplyEnvSection("GIT", config.Git);
        ApplyEnvSection("TUI", config.Tui);
        ApplyEnvSection("SESSION", config.Session);
        ApplyEnvSection("TRAJECTORY", config.Trajectory);

        // API keys can always come from env vars
        TrySetFromEnv(config.Model.Anthropic, nameof(AnthropicConfig.ApiKey), "ANTHROPIC_API_KEY");
        TrySetFromEnv(config.Model.OpenAi, nameof(OpenAiConfig.ApiKey), "OPENAI_API_KEY");
        TrySetFromEnv(config.Model.Azure, nameof(AzureConfig.ApiKey), "AZURE_OPENAI_API_KEY");
    }

    private static void ApplyEnvSection(string section, object target)
    {
        var prefix = $"DOTSY_{section}_";
        var props = target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && IsScalarType(p.PropertyType));

        foreach (var prop in props)
        {
            var envKey = prefix + ToScreamingSnakeCase(prop.Name);
            var val = Environment.GetEnvironmentVariable(envKey);
            if (val is null)
                continue;

            var converted = ConvertValue(val, prop.PropertyType);
            if (converted is not null)
                prop.SetValue(target, converted);
        }
    }

    public static string GetProviderDisplayName(string providerKey) =>
        providerKey.ToLowerInvariant() switch
        {
            "anthropic"    => "Anthropic",
            "openai"       => "OpenAI",
            "ollama"       => "Ollama",
            "azure_openai" => "Azure OpenAI",
            _              => providerKey
        };

    /// <summary>
    /// Returns a human-readable description of where the API key for the active provider
    /// came from: an env variable name, the global config file path, or "not specified".
    /// </summary>
    public static string GetApiKeySource(DotsyConfig config)
    {
        return config.Model.Provider.ToLowerInvariant() switch
        {
            "anthropic"    => KeySource("ANTHROPIC_API_KEY",    config.Model.Anthropic.ApiKey),
            "openai"       => KeySource("OPENAI_API_KEY",       config.Model.OpenAi.ApiKey),
            "azure_openai" => KeySource("AZURE_OPENAI_API_KEY", config.Model.Azure.ApiKey),
            "ollama"       => "no key required",
            _              => KeySource(null, ""),
        };
    }

    private static string KeySource(string? envVar, string configuredValue)
    {
        if (envVar is not null && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(envVar)))
            return $"env {envVar}";
        if (!string.IsNullOrEmpty(configuredValue))
            return GlobalConfigPath;
        return "not specified";
    }

    private static void TrySetFromEnv(object target, string propName, string envVar)
    {
        var val = Environment.GetEnvironmentVariable(envVar);
        if (val is null)
            return;
        var prop = target.GetType().GetProperty(propName);
        prop?.SetValue(target, val);
    }

    private static string ToScreamingSnakeCase(string name)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0)
                sb.Append('_');
            sb.Append(char.ToUpper(name[i]));
        }
        return sb.ToString();
    }

    private static string ToConfigKey(string name) =>
        name == nameof(TuiConfig.LeftPanelWidthPercentage)
            ? "left-panel-width-percentage"
            : ToSnakeCase(name);

    // Misspelled key shipped in earlier builds; still accepted on read so saved configs keep working.
    private static string? LegacyConfigKey(string name) =>
        name == nameof(TuiConfig.LeftPanelWidthPercentage)
            ? "left-poanel-width-percentage"
            : null;

    private static List<McpServerConfig> ParseMcpServers(object serversObj)
    {
        var servers = new List<McpServerConfig>();
        IEnumerable<TomlTable> tables = serversObj switch
        {
            TomlTableArray tableArray => tableArray.OfType<TomlTable>(),
            TomlArray array => array.OfType<TomlTable>(),
            _ => []
        };

        foreach (var table in tables)
        {
            var server = new McpServerConfig();
            ApplyScalars(server, table);

            if (table.TryGetValue("transport", out var transportObj)
                && TryParseTransport(transportObj?.ToString(), out var transport))
            {
                server.Transport = transport;
            }
            else if (!string.IsNullOrWhiteSpace(server.Url))
            {
                server.Transport = McpTransport.Http;
            }

            if (table.TryGetValue("args", out var argsObj) && argsObj is TomlArray args)
                server.Args = [.. args.Select(a => a?.ToString() ?? "").Where(a => a.Length > 0)];

            if (!string.IsNullOrWhiteSpace(server.Name))
                servers.Add(server);
        }

        return servers;
    }

    private static bool TryParseTransport(string? raw, out McpTransport transport)
    {
        transport = McpTransport.Stdio;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        return raw.Trim().ToLowerInvariant() switch
        {
            "stdio" => SetTransport(McpTransport.Stdio, out transport),
            "http" => SetTransport(McpTransport.Http, out transport),
            _ => false
        };
    }

    private static bool SetTransport(McpTransport value, out McpTransport transport)
    {
        transport = value;
        return true;
    }
}
