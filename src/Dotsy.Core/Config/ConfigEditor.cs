using System.Reflection;
using System.Text;
using Tomlyn;
using Tomlyn.Model;

namespace Dotsy.Core.Config;

public static class ConfigEditor
{
    public static readonly string ConfigFilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "dotsy", "config.toml");

    // ── Parameter catalogue ───────────────────────────────────────────────────

    public record ParamDef(
        string Key,
        string Type,
        string Description,
        IReadOnlyList<string>? ValidValues = null,
        string? Range = null);
    public record ParamGroup
    {
        public string Section { get; init; }
        public List<ParamDef> Params { get; init; }
        public ParamGroup(string Section, List<ParamDef> Params)
        {
            this.Section = Section;
            this.Params = [.. Params.Select(p => p with { Key = Section + "." + p.Key })];
        }
    }

    private sealed record ConfigParam(
        ParamDef Definition,
        Func<DotsyConfig, object?> GetValue,
        bool Secret = false,
        Func<object?, bool>? IsEmpty = null,
        Func<object?, string>? Format = null);

    private sealed record ConfigParamGroup(string Section, List<ConfigParam> Params);

    private static ConfigParam Param(
        string key,
        string type,
        string description,
        Func<DotsyConfig, object?> getValue,
        IReadOnlyList<string>? validValues = null,
        string? range = null,
        bool secret = false,
        Func<object?, bool>? isEmpty = null,
        Func<object?, string>? format = null) =>
        new(new ParamDef(key, type, description, validValues, range),
            getValue, secret, isEmpty, format);

    private static readonly List<ConfigParamGroup> Catalog =
    [
        new("model",
        [
            Param("provider", "string", "active provider",
                cfg => cfg.Model.Provider, ProviderConfig.SelectableProviders),
            Param("max_output_tokens_per_request", "int", "max output tokens per request",
                cfg => cfg.Model.MaxOutputTokensPerRequest),
        ]),
        new("model." + ProviderConfig.Anthropic,
        [
            Param("id", "string", "Anthropic model ID, e.g. claude-sonnet-4-6",
                cfg => cfg.Model.Anthropic.Id),
            Param("api_key", "string", $"Anthropic API key (overrides {ProviderConfig.AnthropicEnvVar} env var)",
                cfg => cfg.Model.Anthropic.ApiKey, secret: true),
        ]),
        new("model." + ProviderConfig.OpenAi,
        [
            Param("id", "string", "OpenAI model ID, e.g. gpt-4o",
                cfg => cfg.Model.OpenAi.Id),
            Param("api_key", "string", $"OpenAI API key (overrides {ProviderConfig.OpenAiEnvVar} env var)",
                cfg => cfg.Model.OpenAi.ApiKey, secret: true),
            Param("base_url", "string", "base URL, change for OpenAI-compatible providers",
                cfg => cfg.Model.OpenAi.BaseUrl, isEmpty: _ => false),
        ]),
        new("model." + ProviderConfig.Ollama,
        [
            Param("id", "string", "Ollama model ID, e.g. llama3",
                cfg => cfg.Model.Ollama.Id),
            Param("base_url", "string", "Ollama server URL",
                cfg => cfg.Model.Ollama.BaseUrl, isEmpty: _ => false),
            Param("max_context_tokens", "int", "context window (num_ctx) requested when invoking Ollama",
                cfg => cfg.Model.Ollama.MaxContextTokens),
        ]),
        new("model." + ProviderConfig.Azure,
        [
            Param("id", "string", "Azure model ID",
                cfg => cfg.Model.Azure.Id),
            Param("api_key", "string", "Azure OpenAI API key",
                cfg => cfg.Model.Azure.ApiKey, secret: true),
            Param("endpoint", "string", "Azure endpoint URL",
                cfg => cfg.Model.Azure.Endpoint),
            Param("deployment", "string", "Azure deployment name",
                cfg => cfg.Model.Azure.Deployment),
            Param("api_version", "string", "Azure API version",
                cfg => cfg.Model.Azure.ApiVersion),
        ]),
        new("model." + ProviderConfig.Compatible,
        [
            Param("id", "string", "model ID for compatible provider",
                cfg => cfg.Model.Compatible.Id),
            Param("api_key", "string", "API key for compatible provider",
                cfg => cfg.Model.Compatible.ApiKey, secret: true),
            Param("base_url", "string", "base URL for compatible provider",
                cfg => cfg.Model.Compatible.BaseUrl),
        ]),
        new("model." + ProviderConfig.Gemini,
        [
            Param("id", "string", "Gemini model ID, e.g. gemini-2.5-flash-lite",
                cfg => cfg.Model.Gemini.Id),
            Param("api_key", "string", $"Google Gemini API key (overrides {ProviderConfig.GeminiEnvVar} env var)",
                cfg => cfg.Model.Gemini.ApiKey, secret: true),
        ]),
        new("agent",
        [
            Param("max_turns", "int", "turn limit per session (0 = unlimited)",
                cfg => cfg.Agent.MaxTurns),
            Param("parallel_tools", "bool", "run read-only tools in parallel",
                cfg => cfg.Agent.ParallelTools),
            Param("auto_lint", "bool", "run dotnet build after each write and feed errors back",
                cfg => cfg.Agent.AutoLint),
            Param("auto_test", "bool", "run dotnet test after each write and feed failures back",
                cfg => cfg.Agent.AutoTest),
            Param("nudge_limit", "int", "max consecutive non-terminal text-only turns before stopping",
                cfg => cfg.Agent.NudgeLimit),
            Param("repeat_window_turns", "int", "turn window used to detect repeated tool calls",
                cfg => cfg.Agent.RepeatWindowTurns),
            Param("repeat_threshold", "int", "repeated tool calls allowed before the agent is nudged (0 = disabled)",
                cfg => cfg.Agent.RepeatThreshold),
            Param("max_reflections", "int", "max lint/test reflection cycles per turn",
                cfg => cfg.Agent.MaxReflections),
        ]),
        new("compaction",
        [
            Param("enabled", "bool", "auto-summarise context when usage exceeds threshold",
                cfg => cfg.Compaction.Enabled),
            Param("threshold_pct", "float", "usage fraction that triggers compaction, e.g. 0.80",
                cfg => cfg.Compaction.ThresholdPct,
                format: value => ((float)value!).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)),
            Param("reserve_tokens", "int", "tokens reserved for the compaction summary request",
                cfg => cfg.Compaction.ReserveTokens),
            Param("keep_recent_tokens", "int", "recent tokens to retain verbatim after compaction",
                cfg => cfg.Compaction.KeepRecentTokens),
            Param("tool_pair_summarize", "bool", "collapse old tool call/result pairs into one-line notes",
                cfg => cfg.Compaction.ToolPairSummarize),
        ]),
        new("git",
        [
            Param("auto_stage", "bool", "auto-stage modified files after agent writes",
                cfg => cfg.Git.AutoStage),
            Param("diff_context_lines", "int", "context lines shown in diffs",
                cfg => cfg.Git.DiffContextLines),
        ]),
        new("session",
        [
            Param("log_enabled", "bool", "enable session logging (default: true)",
                cfg => cfg.Session.LogEnabled),
            Param("log_dir", "string", "session log directory (relative to project or absolute)",
                cfg => cfg.Session.LogDir),
            Param("cleanup_days", "int", "auto-delete sessions older than N days (0 = never)",
                cfg => cfg.Session.CleanupDays),
        ]),
        new("trajectory",
        [
            Param("enabled", "bool", "write OpenCode-compatible trajectory JSON after each session",
                cfg => cfg.Trajectory.Enabled),
            Param("dir", "string", "trajectory export directory (relative to project or absolute)",
                cfg => cfg.Trajectory.Dir),
        ]),
        new("tui",
        [
            Param("left_panel_width_percentage", "int", "conversation panel width percentage",
                cfg => cfg.Tui.LeftPanelWidthPercentage),
            Param("verbose", "bool", "show tool calls and results inline in the conversation panel",
                cfg => cfg.Tui.Verbose),
            Param("theme", "string", "color theme",
                cfg => cfg.Tui.Theme, ["dark", "light", "system", "borland"]),
        ]),
    ];

    public static readonly List<ParamGroup> ParamList =
    [
        .. Catalog.Select(group =>
            new ParamGroup(group.Section, [.. group.Params.Select(param => param.Definition)]))
    ];

    public static ParamDef? FindParam(string key) =>
        ParamList
            .SelectMany(g => g.Params)
            .FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<string> GetValidValues(ParamDef param)
    {
        if (param.ValidValues is { Count: > 0 })
            return param.ValidValues;

        if (param.Type.Equals("bool", StringComparison.OrdinalIgnoreCase))
            return ["true", "false"];

        return [];
    }

    public static string GetValueHint(ParamDef param)
    {
        var hint = param.Description;
        var validValues = GetValidValues(param);

        if (validValues.Count > 0)
            hint += $" (valid: {string.Join(" | ", validValues)})";

        if (!string.IsNullOrWhiteSpace(param.Range))
            hint += $" (range: {param.Range})";

        return hint;
    }

    // ── Display ───────────────────────────────────────────────────────────────

    public record Kv(string Key, string Value, bool Empty);
    public record Section(string Header, List<Kv> Kvs);

    public static List<Section> GetSections(DotsyConfig cfg) =>
    [
        .. Catalog.Select(group => new Section(
            group.Section,
            [.. group.Params.Select(param => ToKv(param, cfg))]))
    ];

    private static Kv ToKv(ConfigParam param, DotsyConfig cfg)
    {
        var value = param.GetValue(cfg);
        var empty = param.IsEmpty?.Invoke(value) ?? value is null or "";
        var formatted = param.Secret
            ? Mask(value as string ?? "")
            : param.Format?.Invoke(value) ?? FormatValue(value);
        return new Kv(param.Definition.Key, formatted, empty);
    }

    // ── Edit ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sets a config value by dot-key (e.g. "model.provider", "model.anthropic.api_key").
    /// Updates the in-memory config object and persists to whichever config file already
    /// contains the key (project config if present, global config otherwise).
    /// Returns (true, filePath) on success or (false, errorMessage) on failure.
    /// </summary>
    public static (bool Ok, string Message) Set(DotsyConfig cfg, string dotKey, string rawValue,
        string? projectConfigPath = null)
    {
        var parts = dotKey.ToLowerInvariant().Split('.');
        if (parts.Length < 2)
            return (false, $"invalid key '{dotKey}' — use format section.key (e.g. model.provider)");
        if (parts.Length > 3)
            return (false, "key nesting too deep — maximum three levels (e.g. model.anthropic.api_key)");

        var memErr = ApplyInMemory(cfg, parts, rawValue);
        if (memErr is not null) return (false, memErr);

        var targetFile = ResolveTargetFile(parts, projectConfigPath);
        var writeErr = PersistToml(parts, rawValue, targetFile);
        if (writeErr is not null) return (false, writeErr);

        return (true, targetFile);
    }

    public static (bool Ok, bool Unchanged, string CurrentValue, string? Message) CheckUnchanged(
        DotsyConfig cfg,
        string dotKey,
        string rawValue)
    {
        var parts = dotKey.ToLowerInvariant().Split('.');
        if (parts.Length < 2)
            return (false, false, "", $"invalid key '{dotKey}' — use format section.key (e.g. model.provider)");
        if (parts.Length > 3)
            return (false, false, "", "key nesting too deep — maximum three levels (e.g. model.anthropic.api_key)");

        var propErr = ResolveWritableProperty(cfg, parts, out var target, out var prop);
        if (propErr is not null)
            return (false, false, "", propErr);

        var converted = ConvertValue(rawValue, prop!.PropertyType);
        if (converted is null)
            return (false, false, "", $"cannot convert '{rawValue}' to {prop.PropertyType.Name}");

        var current = prop.GetValue(target);
        return (true, Equals(current, converted), FormatValue(current), null);
    }

    // Chooses which file a key is written to. A project config takes precedence when one exists, so
    // settings land alongside the project rather than in the user-global file. Secrets are the
    // exception: api keys always go to the global config because project configs are loaded without
    // secrets (allowSecrets: false) and may be committed to source control.
    private static string ResolveTargetFile(string[] parts, string? projectConfigPath)
    {
        var isSecret = parts[^1].Equals("api_key", StringComparison.OrdinalIgnoreCase);
        if (!isSecret && projectConfigPath is not null && File.Exists(projectConfigPath))
            return projectConfigPath;

        return ConfigFilePath;
    }

    // ── In-memory update ──────────────────────────────────────────────────────

    private static string? ApplyInMemory(DotsyConfig cfg, string[] parts, string rawValue)
    {
        var propErr = ResolveWritableProperty(cfg, parts, out var target, out var prop);
        if (propErr is not null)
            return propErr;

        var converted = ConvertValue(rawValue, prop!.PropertyType);
        if (converted is null)
            return $"cannot convert '{rawValue}' to {prop.PropertyType.Name}";

        prop.SetValue(target, converted);
        return null;
    }

    private static string? ResolveWritableProperty(
        DotsyConfig cfg,
        string[] parts,
        out object? target,
        out PropertyInfo? prop)
    {
        target = ResolveTarget(cfg, parts);
        prop = null;

        if (target is null)
            return parts.Length == 3 && parts[0] == "model"
                ? $"unknown sub-section 'model.{parts[1]}'"
                : $"unknown config section '{parts[0]}'";

        var propName = ToPropertyName(parts[^1]);
        prop = target.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
        if (prop is null || !prop.CanWrite)
            return $"unknown config key '{string.Join(".", parts)}'";

        return null;
    }

    private static object? ResolveTarget(DotsyConfig cfg, string[] parts)
    {
        if (parts.Length == 3 && parts[0] == "model")
        {
            return parts[1] switch
            {
                ProviderConfig.Anthropic  => cfg.Model.Anthropic,
                ProviderConfig.OpenAi     => cfg.Model.OpenAi,
                ProviderConfig.Azure      => cfg.Model.Azure,
                ProviderConfig.Ollama     => cfg.Model.Ollama,
                ProviderConfig.Compatible => cfg.Model.Compatible,
                ProviderConfig.Gemini     => cfg.Model.Gemini,
                _ => null
            };
        }

        return parts[0] switch
        {
            "model"      => cfg.Model,
            "agent"      => cfg.Agent,
            "compaction" => cfg.Compaction,
            "retrieval"  => cfg.Retrieval,
            "git"        => cfg.Git,
            "tui"        => cfg.Tui,
            "session"    => cfg.Session,
            "trajectory" => cfg.Trajectory,
            _ => null
        };
    }

    // ── TOML persistence ──────────────────────────────────────────────────────

    private static string? PersistToml(string[] parts, string rawValue, string targetFile)
    {
        var table = new TomlTable();
        if (File.Exists(targetFile))
        {
            var existing = File.ReadAllText(targetFile);
            if (TomlSerializer.TryDeserialize(existing, out TomlTable? t) && t is not null)
                table = t;
        }

        // Navigate / create nested tables
        var section = parts[0];
        if (!table.ContainsKey(section) || table[section] is not TomlTable)
            table[section] = new TomlTable();
        var sectionTable = (TomlTable)table[section];

        if (parts.Length == 2)
        {
            sectionTable[parts[1]] = ToTomlScalar(rawValue);
        }
        else // 3 parts
        {
            if (!sectionTable.ContainsKey(parts[1]) || sectionTable[parts[1]] is not TomlTable)
                sectionTable[parts[1]] = new TomlTable();
            ((TomlTable)sectionTable[parts[1]])[parts[2]] = ToTomlScalar(rawValue);
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            File.WriteAllText(targetFile, SerializeToml(table));
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    // ── TOML serializer ───────────────────────────────────────────────────────

    private static string SerializeToml(TomlTable root)
    {
        var sb = new StringBuilder();
        var sections = new List<(string Header, TomlTable Table)>();

        foreach (var kv in root)
        {
            if (kv.Value is TomlTable t) sections.Add(($"[{kv.Key}]", t));
            else AppendKv(sb, kv.Key, kv.Value);
        }

        foreach (var (header, section) in sections)
        {
            sb.AppendLine();
            sb.AppendLine(header);
            var nested = new List<(string Header, TomlTable Table)>();
            foreach (var kv in section)
            {
                if (kv.Value is TomlTable t) nested.Add(($"{header[..^1]}.{kv.Key}]", t));
                else AppendKv(sb, kv.Key, kv.Value);
            }
            foreach (var (nh, nt) in nested)
            {
                sb.AppendLine();
                sb.AppendLine(nh);
                foreach (var kv in nt)
                    AppendKv(sb, kv.Key, kv.Value);
            }
        }

        return sb.ToString();
    }

    private static void AppendKv(StringBuilder sb, string key, object value)
    {
        var v = value switch
        {
            bool b   => b ? "true" : "false",
            long l   => l.ToString(),
            int i    => i.ToString(),
            double d => d.ToString("G", System.Globalization.CultureInfo.InvariantCulture),
            float f  => f.ToString("G", System.Globalization.CultureInfo.InvariantCulture),
            string s => $"\"{s.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"",
            _        => $"\"{value}\""
        };
        sb.AppendLine($"{key} = {v}");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool TryParseHumanNumeric(string raw, out long result)
    {
        result = 0;
        if (string.IsNullOrEmpty(raw)) return false;
        char last = char.ToLowerInvariant(raw[^1]);

        var multiplier = 1;
        if (last == 'k')
        {
            raw = raw[..^1];
            multiplier = 1024;
        }
        if (last == 'm')
        {                   
            raw = raw[..^1];
            multiplier = 1024 * 1024;
        }

        if (long.TryParse(raw, out var val))
        {
            result = val * multiplier;
            return true;
        }
        return false;
    }

    private static object ToTomlScalar(string raw)
    {
        if (raw.Equals("true", StringComparison.OrdinalIgnoreCase))  return true;
        if (raw.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
        if (TryParseHumanNumeric(raw, out var hn)) return hn;
        if (long.TryParse(raw, out var l)) return l;
        if (double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d)) return d;
        return raw;
    }

    private static string FormatValue(object? value) =>
        value switch
        {
            null => "",
            bool b => b ? "true" : "false",
            IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => value.ToString() ?? ""
        };


    private static object? ConvertValue(string raw, Type t)
    {
        try
        {
            if (t == typeof(string)) return raw;

            if (TryParseHumanNumeric(raw, out var hn))
            {
                if (t == typeof(int)) return (int)hn;
                if (t == typeof(long)) return hn;
            }

            if (t == typeof(int))    return int.Parse(raw);
            if (t == typeof(long))   return long.Parse(raw);
            if (t == typeof(float))  return float.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
            if (t == typeof(double)) return double.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
            if (t == typeof(bool))   return raw is "true" or "1" or "yes";
        }
        catch { }
        return null;
    }

    private static string ToPropertyName(string snake) =>
        string.Concat(snake.Split('_').Select(w => w.Length > 0 ? char.ToUpper(w[0]) + w[1..] : w));

    private static string Mask(string key) =>
        string.IsNullOrEmpty(key) ? "(not set)" : key[..Math.Min(8, key.Length)] + "****";
}
