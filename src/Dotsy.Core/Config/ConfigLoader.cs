using System.Reflection;
using Tomlyn;
using Tomlyn.Model;

namespace Dotsy.Core.Config;

public static class ConfigLoader
{
    public static readonly string GlobalConfigPath =
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
        if (!TomlSerializer.TryDeserialize(text, out TomlTable? table) || table is null)
            return;

        if (table.TryGetValue("model", out var modelObj) && modelObj is TomlTable model)
        {
            ApplyScalars(config.Model, model);
            // Non-secret scalars (model id, base urls, …) load from any config; api keys
            // only from the global config (allowSecrets) so project configs never carry them.
            ApplyModelSubSection(config.Model.Anthropic, model, ProviderConfig.Anthropic, allowSecrets);
            ApplyModelSubSection(config.Model.OpenAi, model, ProviderConfig.OpenAi, allowSecrets);
            ApplyModelSubSection(config.Model.Azure, model, ProviderConfig.Azure, allowSecrets);
            ApplyModelSubSection(config.Model.Ollama, model, ProviderConfig.Ollama, allowSecrets);
            ApplyModelSubSection(config.Model.Compatible, model, ProviderConfig.Compatible, allowSecrets);
            ApplyModelSubSection(config.Model.Gemini, model, ProviderConfig.Gemini, allowSecrets);
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

    private static void ApplyModelSubSection(object target, TomlTable parent, string key, bool allowSecrets)
    {
        if (!parent.TryGetValue(key, out var nested) || nested is not TomlTable table)
            return;
        // ApiKey is the only secret on a provider section; skip it unless secrets are allowed.
        ApplyScalars(target, table, allowSecrets ? null : nameof(AnthropicConfig.ApiKey));
    }

    private static void ApplyScalars(object target, TomlTable table, string? excludeProp = null)
    {
        var props = target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && IsScalarType(p.PropertyType) && p.Name != excludeProp);

        foreach (var prop in props)
        {
            var tomlKey = ToSnakeCase(prop.Name);
            if (!table.TryGetValue(tomlKey, out var raw))
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
        ApplyEnvSection("MODEL_GEMINI", config.Model.Gemini);
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
        TrySetFromEnv(config.Model.Anthropic, nameof(AnthropicConfig.ApiKey), ProviderConfig.AnthropicEnvVar);
        TrySetFromEnv(config.Model.OpenAi, nameof(OpenAiConfig.ApiKey), ProviderConfig.OpenAiEnvVar);
        TrySetFromEnv(config.Model.Azure, nameof(AzureConfig.ApiKey), ProviderConfig.AzureEnvVar);
        TrySetFromEnv(config.Model.Gemini, nameof(GeminiConfig.ApiKey), ProviderConfig.GeminiEnvVar);
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

    public const string NoKeyRequired = "no key required";
    public const string KeyNotSpecified = "not specified";

    /// <summary>
    /// Returns a human-readable description of where the API key for the active provider
    /// came from: an env variable name, the global config file path, or "not specified".
    /// </summary>
    public static string GetApiKeySource(DotsyConfig config)
    {
        var provider = config.Model.Provider.ToLowerInvariant();

        if (ProviderConfig.Ollama == provider) return NoKeyRequired;

        var envVar = ProviderConfig.ProviderEnvVar(provider);

        if (envVar is not null && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(envVar)))
            return $"env {envVar}";

        if (!string.IsNullOrEmpty(config.Model.ActiveModel.ApiKey))
            return GlobalConfigPath;
        return KeyNotSpecified;
    }

    /// <summary>Resolved location of each config file plus, for every catalogued key, which layer
    /// currently provides its value. Lets callers (e.g. <c>/self</c>) tell the user which file to edit.</summary>
    public sealed record ConfigSourceInfo(
        string GlobalPath,
        bool GlobalExists,
        string? ProjectPath,
        bool ProjectExists,
        IReadOnlyDictionary<string, string> KeySources);

    /// <summary>
    /// Determines, for every key in <see cref="ConfigEditor.ParamList"/>, where its effective value
    /// comes from (env &gt; project &gt; default). API-key secrets are never sourced from the
    /// project config, matching <see cref="Load"/>.
    /// Source values are the literal file path, <c>env:VAR</c>, or <c>default</c>.
    /// The global config file path/existence is reported separately via <see cref="ConfigSourceInfo"/>.
    /// </summary>
    public static ConfigSourceInfo GetConfigSources(string cwd)
    {
        var projectPath = FindProjectConfig(cwd);
        var projectKeys = projectPath is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : FlattenTomlKeys(projectPath);

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in ConfigEditor.ParamList)
            foreach (var p in group.Params)
                map[p.Key] = ResolveKeySource(p.Key, projectKeys, projectPath);

        return new ConfigSourceInfo(
            GlobalConfigPath, File.Exists(GlobalConfigPath),
            projectPath, projectPath is not null, map);
    }

    private static string ResolveKeySource(
        string key, HashSet<string> projectKeys, string? projectPath)
    {
        var secret = key.EndsWith(".api_key", StringComparison.OrdinalIgnoreCase);

        // Env overrides everything; check both the generic DOTSY_ overlay and the well-known key vars.
        var envVar = EnvVarForKey(key);
        if (HasEnv(envVar))
            return $"env:{envVar}";
        if (secret && GetKnownSecretEnvVar(key) is { } secretEnv && HasEnv(secretEnv))
            return $"env:{secretEnv}";

        // Project config is applied after global, but never carries secrets.
        if (!secret && projectPath is not null && projectKeys.Contains(key))
            return projectPath;
        return "default";
    }

    private static bool HasEnv(string name) =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name));

    // Mirrors the DOTSY_<SECTION>_<KEY> overlay naming in ApplyEnvSection for a dotted config key.
    private static string EnvVarForKey(string key)
    {
        var parts = key.Split('.');
        var prop = parts[^1].Replace('-', '_').ToUpperInvariant();
        var section = string.Join("_", parts[..^1].Select(s => s.Replace('-', '_').ToUpperInvariant()));
        return section.Length == 0 ? $"DOTSY_{prop}" : $"DOTSY_{section}_{prop}";
    }

    public static string? GetKnownSecretEnvVar(string key)
    {
        // Extract the provider from the key, e.g. "model.anthropic.api_key" => "anthropic"
        // Check if this is "api_key"
        // Then map the provider to its known environment variable name.
        return key.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) switch
        {
            ["model", var provider, "api_key"] => ProviderConfig.ProviderEnvVar(provider),
            _ => null
        };
    }

    public static bool IsSecretKey(string key) =>
        key.EndsWith(".api_key", StringComparison.OrdinalIgnoreCase)
        || key.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || key.Contains("auth_token", StringComparison.OrdinalIgnoreCase)
        || key.Contains("access_token", StringComparison.OrdinalIgnoreCase)
        || key.Contains("bearer", StringComparison.OrdinalIgnoreCase)
        || key.Contains("password", StringComparison.OrdinalIgnoreCase);

    // Flattens a TOML file into the set of dotted leaf keys it sets (e.g. "model.anthropic.api_key").
    private static HashSet<string> FlattenTomlKeys(string path)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
            return keys;
        try
        {
            var text = File.ReadAllText(path);
            if (TomlSerializer.TryDeserialize(text, out TomlTable? table) && table is not null)
                FlattenTable(table, "", keys);
        }
        catch { }
        return keys;
    }

    private static void FlattenTable(TomlTable table, string prefix, HashSet<string> keys)
    {
        foreach (var kv in table)
        {
            var key = prefix.Length == 0 ? kv.Key : $"{prefix}.{kv.Key}";
            if (kv.Value is TomlTable child)
                FlattenTable(child, key, keys);
            else
                keys.Add(key);
        }
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
        McpTransport? tr = raw?.Trim().ToLowerInvariant() switch
        {
            "stdio" => McpTransport.Stdio,
            "http" => McpTransport.Http,
            _ => null
        };

        transport = tr ?? McpTransport.Stdio;

        return tr.HasValue;
    }
}
