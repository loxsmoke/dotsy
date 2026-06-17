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

    public record ParamDef(string Key, string Type, string Description);
    public record ParamGroup(string Section, List<ParamDef> Params);

    public static readonly List<ParamGroup> ParamList =
    [
        new("model",
        [
            new("model.provider",   "string", "active provider: anthropic | openai | ollama | azure_openai | compatible | gemini"),
            new("model.max_output_tokens_per_request", "int", "max output tokens per request"),
        ]),
        new("model.anthropic",
        [
            new("model.anthropic.id",      "string", "Anthropic model ID, e.g. claude-sonnet-4-6"),
            new("model.anthropic.api_key", "string", "Anthropic API key (overrides ANTHROPIC_API_KEY env var)"),
        ]),
        new("model.openai",
        [
            new("model.openai.id",       "string", "OpenAI model ID, e.g. gpt-4o"),
            new("model.openai.api_key",  "string", "OpenAI API key (overrides OPENAI_API_KEY env var)"),
            new("model.openai.base_url", "string", "base URL, change for OpenAI-compatible providers"),
        ]),
        new("model.ollama",
        [
            new("model.ollama.id",       "string", "Ollama model ID, e.g. llama3"),
            new("model.ollama.base_url", "string", "Ollama server URL"),
        ]),
        new("model.azure",
        [
            new("model.azure.id",         "string", "Azure model ID"),
            new("model.azure.api_key",    "string", "Azure OpenAI API key"),
            new("model.azure.endpoint",   "string", "Azure endpoint URL"),
            new("model.azure.deployment", "string", "Azure deployment name"),
            new("model.azure.api_version","string", "Azure API version"),
        ]),
        new("model.compatible",
        [
            new("model.compatible.id",       "string", "model ID for compatible provider"),
            new("model.compatible.api_key",  "string", "API key for compatible provider"),
            new("model.compatible.base_url", "string", "base URL for compatible provider"),
        ]),
        new("model.gemini",
        [
            new("model.gemini.id",      "string", "Gemini model ID, e.g. gemini-2.5-flash-lite"),
            new("model.gemini.api_key", "string", "Google Gemini API key (overrides GEMINI_API_KEY env var)"),
        ]),
        new("agent",
        [
            new("agent.max_turns",       "int",   "turn limit per session (0 = unlimited)"),
            new("agent.parallel_tools",  "bool",  "run read-only tools in parallel"),
            new("agent.auto_lint",       "bool",  "run dotnet build after each write and feed errors back"),
            new("agent.auto_test",       "bool",  "run dotnet test after each write and feed failures back"),
            new("agent.nudge_limit",     "int",   "max consecutive text-only turns before stopping"),
            new("agent.max_reflections", "int",   "max lint/test reflection cycles per turn"),
        ]),
        new("compaction",
        [
            new("compaction.enabled",            "bool",  "auto-summarise context when usage exceeds threshold"),
            new("compaction.threshold_pct",      "float", "usage fraction that triggers compaction, e.g. 0.80"),
            new("compaction.reserve_tokens",     "int",   "tokens reserved for the compaction summary request"),
            new("compaction.keep_recent_tokens", "int",   "recent tokens to retain verbatim after compaction"),
        ]),
        new("git",
        [
            new("git.auto_stage",         "bool", "auto-stage modified files after agent writes"),
            new("git.diff_context_lines", "int",  "context lines shown in diffs"),
        ]),
        new("session",
        [
            new("session.log_enabled",  "bool",   "enable session logging (default: true)"),
            new("session.log_dir",      "string", "session log directory (relative to project or absolute)"),
            new("session.cleanup_days", "int",    "auto-delete sessions older than N days (0 = never)"),
        ]),
        new("trajectory",
        [
            new("trajectory.enabled", "bool",   "write OpenCode-compatible trajectory JSON after each session"),
            new("trajectory.dir",     "string", "trajectory export directory (relative to project or absolute)"),
        ]),
        new("tui",
        [
            new("tui.left-panel-width-percentage", "int", "conversation panel width percentage"),
            new("tui.verbose", "bool", "show tool calls and results inline in the conversation panel"),
            new("tui.theme", "string", "color theme: dark | light | system | borland"),
        ]),
    ];

    // ── Display ───────────────────────────────────────────────────────────────

    public record Kv(string Key, string Value, bool Empty);
    public record Section(string Header, List<Kv> Kvs);

    public static List<Section> GetSections(DotsyConfig cfg) =>
    [
        new("model",
        [
            new("provider",   cfg.Model.Provider,             string.IsNullOrEmpty(cfg.Model.Provider)),
            new("max_output_tokens_per_request", cfg.Model.MaxOutputTokensPerRequest.ToString(), false),
        ]),
        new("model.anthropic",
        [
            new("id",        cfg.Model.Anthropic.Id,           string.IsNullOrEmpty(cfg.Model.Anthropic.Id)),
            new("api_key",   Mask(cfg.Model.Anthropic.ApiKey), string.IsNullOrEmpty(cfg.Model.Anthropic.ApiKey)),
        ]),
        new("model.openai",
        [
            new("id",       cfg.Model.OpenAi.Id,            string.IsNullOrEmpty(cfg.Model.OpenAi.Id)),
            new("api_key",  Mask(cfg.Model.OpenAi.ApiKey),  string.IsNullOrEmpty(cfg.Model.OpenAi.ApiKey)),
            new("base_url", cfg.Model.OpenAi.BaseUrl,       false),
        ]),
        new("model.ollama",
        [
            new("id",       cfg.Model.Ollama.Id,      string.IsNullOrEmpty(cfg.Model.Ollama.Id)),
            new("base_url", cfg.Model.Ollama.BaseUrl, false),
        ]),
        new("model.azure",
        [
            new("id",         cfg.Model.Azure.Id,            string.IsNullOrEmpty(cfg.Model.Azure.Id)),
            new("api_key",    Mask(cfg.Model.Azure.ApiKey),  string.IsNullOrEmpty(cfg.Model.Azure.ApiKey)),
            new("endpoint",   cfg.Model.Azure.Endpoint,      string.IsNullOrEmpty(cfg.Model.Azure.Endpoint)),
            new("deployment", cfg.Model.Azure.Deployment,    string.IsNullOrEmpty(cfg.Model.Azure.Deployment)),
        ]),
        new("model.compatible",
        [
            new("id",       cfg.Model.Compatible.Id,           string.IsNullOrEmpty(cfg.Model.Compatible.Id)),
            new("api_key",  Mask(cfg.Model.Compatible.ApiKey),  string.IsNullOrEmpty(cfg.Model.Compatible.ApiKey)),
            new("base_url", cfg.Model.Compatible.BaseUrl,       string.IsNullOrEmpty(cfg.Model.Compatible.BaseUrl)),
        ]),
        new("model.gemini",
        [
            new("id",      cfg.Model.Gemini.Id,           string.IsNullOrEmpty(cfg.Model.Gemini.Id)),
            new("api_key", Mask(cfg.Model.Gemini.ApiKey),  string.IsNullOrEmpty(cfg.Model.Gemini.ApiKey)),
        ]),
        new("agent",
        [
            new("max_turns",       cfg.Agent.MaxTurns.ToString(),                   false),
            new("parallel_tools",  cfg.Agent.ParallelTools.ToString().ToLower(),    false),
            new("auto_lint",       cfg.Agent.AutoLint.ToString().ToLower(),         false),
            new("auto_test",       cfg.Agent.AutoTest.ToString().ToLower(),         false),
            new("nudge_limit",     cfg.Agent.NudgeLimit.ToString(),                 false),
            new("max_reflections", cfg.Agent.MaxReflections.ToString(),             false),
        ]),
        new("compaction",
        [
            new("enabled",             cfg.Compaction.Enabled.ToString().ToLower(),        false),
            new("threshold_pct",       cfg.Compaction.ThresholdPct.ToString("F2"),         false),
            new("reserve_tokens",      cfg.Compaction.ReserveTokens.ToString(),            false),
            new("keep_recent_tokens",  cfg.Compaction.KeepRecentTokens.ToString(),         false),
        ]),
        new("git",
        [
            new("auto_stage",         cfg.Git.AutoStage.ToString().ToLower(),      false),
            new("diff_context_lines", cfg.Git.DiffContextLines.ToString(),         false),
        ]),
        new("session",
        [
            new("log_enabled",  cfg.Session.LogEnabled.ToString().ToLower(), false),
            new("log_dir",      cfg.Session.LogDir, string.IsNullOrEmpty(cfg.Session.LogDir)),
            new("cleanup_days", cfg.Session.CleanupDays.ToString(), false),
        ]),
        new("trajectory",
        [
            new("enabled", cfg.Trajectory.Enabled.ToString().ToLower(), false),
            new("dir",     cfg.Trajectory.Dir, string.IsNullOrEmpty(cfg.Trajectory.Dir)),
        ]),
        new("tui",
        [
            new("left-panel-width-percentage", cfg.Tui.LeftPanelWidthPercentage.ToString(), false),
            new("verbose", cfg.Tui.Verbose.ToString().ToLower(), false),
            new("theme", cfg.Tui.Theme, string.IsNullOrEmpty(cfg.Tui.Theme)),
        ]),
    ];

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

    // Returns the project config path if the key is already defined there, otherwise global.
    private static string ResolveTargetFile(string[] parts, string? projectConfigPath)
    {
        if (projectConfigPath is null || !File.Exists(projectConfigPath))
            return ConfigFilePath;

        try
        {
            var text = File.ReadAllText(projectConfigPath);
            if (!Toml.TryToModel(text, out TomlTable? table, out _, projectConfigPath) || table is null)
                return ConfigFilePath;

            if (!table.TryGetValue(parts[0], out var s0) || s0 is not TomlTable section)
                return ConfigFilePath;

            if (parts.Length == 2)
                return section.ContainsKey(parts[1]) ? projectConfigPath : ConfigFilePath;

            // 3 parts: section.subsection.key
            if (!section.TryGetValue(parts[1], out var s1) || s1 is not TomlTable sub)
                return ConfigFilePath;

            return sub.ContainsKey(parts[2]) ? projectConfigPath : ConfigFilePath;
        }
        catch
        {
            return ConfigFilePath;
        }
    }

    // ── In-memory update ──────────────────────────────────────────────────────

    private static string? ApplyInMemory(DotsyConfig cfg, string[] parts, string rawValue)
    {
        object? target;

        if (parts.Length == 3 && parts[0] == "model")
        {
            target = parts[1] switch
            {
                "anthropic"  => (object)cfg.Model.Anthropic,
                "openai"     => cfg.Model.OpenAi,
                "azure"      => cfg.Model.Azure,
                "ollama"     => cfg.Model.Ollama,
                "compatible" => cfg.Model.Compatible,
                _ => null
            };
            if (target is null)
                return $"unknown sub-section 'model.{parts[1]}'";
        }
        else
        {
            target = parts[0] switch
            {
                "model"      => (object)cfg.Model,
                "agent"      => cfg.Agent,
                "compaction" => cfg.Compaction,
                "retrieval"  => cfg.Retrieval,
                "git"        => cfg.Git,
                "tui"        => cfg.Tui,
                "session"    => cfg.Session,
                "trajectory" => cfg.Trajectory,
                _ => null
            };
            if (target is null)
                return $"unknown config section '{parts[0]}'";
        }

        var propName = ToPropertyName(parts[^1]);
        var prop = target.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
        if (prop is null || !prop.CanWrite)
            return $"unknown config key '{string.Join(".", parts)}'";

        var converted = ConvertValue(rawValue, prop.PropertyType);
        if (converted is null)
            return $"cannot convert '{rawValue}' to {prop.PropertyType.Name}";

        prop.SetValue(target, converted);
        return null;
    }

    // ── TOML persistence ──────────────────────────────────────────────────────

    private static string? PersistToml(string[] parts, string rawValue, string targetFile)
    {
        var table = new TomlTable();
        if (File.Exists(targetFile))
        {
            var existing = File.ReadAllText(targetFile);
            if (Toml.TryToModel(existing, out TomlTable? t, out _, targetFile) && t is not null)
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

    private static object ToTomlScalar(string raw)
    {
        if (raw == "true")  return true;
        if (raw == "false") return false;
        if (long.TryParse(raw, out var l)) return l;
        if (double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d)) return d;
        return raw;
    }

    private static object? ConvertValue(string raw, Type t)
    {
        try
        {
            if (t == typeof(string)) return raw;
            if (t == typeof(int))    return int.Parse(raw);
            if (t == typeof(long))   return long.Parse(raw);
            if (t == typeof(float))  return float.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
            if (t == typeof(double)) return double.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
            if (t == typeof(bool))   return raw is "true" or "1" or "yes";
        }
        catch { }
        return null;
    }

    private static string ToPascalCase(string snake) =>
        string.Concat(snake.Split('_').Select(w => w.Length > 0 ? char.ToUpper(w[0]) + w[1..] : w));

    private static string ToPropertyName(string key) =>
        key == "left-panel-width-percentage"
            ? nameof(TuiConfig.LeftPanelWidthPercentage)
            : ToPascalCase(key);

    private static string Mask(string key) =>
        string.IsNullOrEmpty(key) ? "(not set)" : key[..Math.Min(8, key.Length)] + "****";
}
