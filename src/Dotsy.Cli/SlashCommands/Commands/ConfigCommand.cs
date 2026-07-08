using Dotsy.Cli.SlashCommands.Interfaces;
using Dotsy.Cli.Tui;
using Dotsy.Cli.Tui.Colors;
using Dotsy.Core.Config;
using Dotsy.Core.Utils;

namespace Dotsy.Cli.SlashCommands;

/// <summary>
/// <c>/config</c> — views the active config, lists settable keys, or sets a key. Setting a
/// <c>model.*</c> key reloads the provider; setting <c>tui.theme</c> re-themes live.
/// </summary>
internal sealed class ConfigCommand : ISlashCommand
{
    public const string CommandName = "config";
    public string Name => CommandName;

    public IReadOnlyList<SlashCommandUsage> Usages =>
    [
        new($"/{Name}", "Show the active config file path and current configuration values grouped by section."),
        new($"/{Name} list", "List every settable configuration key, type, and description."),
        new($"/{Name} <key> <value>", "Update a config value via ConfigEditor.Set."),
    ];

    public void Execute(ISlashCommandHost host, string args)
    {
        var cfg = TuiSessionContext.Config;
        if (string.IsNullOrEmpty(args))
        {
            ShowAll(host, cfg);
        }
        else if (args.Trim() == "list")
        {
            ShowKeyList(host);
        }
        else
        {
            SetValue(host, cfg, args);
        }
    }

    public IReadOnlyList<CompletionItem> Complete(ISlashCommandHost host, string partial)
    {
        var text = partial.TrimStart();
        if (string.IsNullOrEmpty(text))
            return [.. TopLevelCompletions("")];

        var firstSpace = text.IndexOf(' ');
        if (firstSpace < 0)
        {
            var keyMatches = AllParams()
                .Where(p => p.Key.StartsWithNoCase(text))
                .Select(p => new CompletionItem(p.Key, $"/{Name} " + p.Key + " "));

            return TopLevelCompletions(text)
                .Concat(keyMatches)
                .DistinctBy(c => c.Replacement, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var first = text[..firstSpace].Trim();
        var rest = text[(firstSpace + 1)..];

        if (FindGroup(first) is { } group)
        {
            return group.Params
                .Where(p => p.Key.StartsWithNoCase(rest.TrimStart())
                    || p.Key[(group.Section.Length + 1)..].StartsWithNoCase(rest.TrimStart()))
                .Select(p => new CompletionItem(p.Key, $"/{Name} " + p.Key + " "))
                .ToList();
        }

        if (FindParam(first) is { } param)
        {
            var prefix = rest.TrimStart();
            return ConfigEditor.GetValidValues(param)
                .Where(v => v.StartsWithNoCase(prefix))
                .Select(v => new CompletionItem(v, $"/{Name} " + param.Key + " " + v))
                .ToList();
        }

        return [];
    }

    private static IEnumerable<CompletionItem> TopLevelCompletions(string prefix)
    {
        if ("list".StartsWithNoCase(prefix))
            yield return new CompletionItem("list", $"/{CommandName} list");

        foreach (var group in ConfigEditor.ParamList
            .Where(g => g.Section.StartsWithNoCase(prefix)))
            yield return new CompletionItem(group.Section, $"/{CommandName} " + group.Section + " ");
    }

    private static IEnumerable<ConfigEditor.ParamDef> AllParams() =>
        ConfigEditor.ParamList.SelectMany(g => g.Params);

    private static ConfigEditor.ParamGroup? FindGroup(string section) =>
        ConfigEditor.ParamList.FirstOrDefault(g =>
            g.Section.StartsWithNoCase(section));

    private static ConfigEditor.ParamDef? FindParam(string key) =>
        AllParams().FirstOrDefault(p => p.Key.StartsWithNoCase(key));

    private static void ShowAll(ISlashCommandHost host, DotsyConfig cfg)
    {
        // The active provider's model section (e.g. [model.openai]) is highlighted green.
        var activeProvider = cfg.Model.Provider.ToLowerInvariant() switch
        {
            "azure_openai" => "azure",
            var p => p,
        };
        var activeSection = $"model.{activeProvider}";

        host.Write($"  {ConfigEditor.ConfigFilePath}\n\n", Palette.Dim);
        foreach (var section in ConfigEditor.GetSections(cfg))
        {
            var headerColor = section.Header.StartsWithNoCase(activeSection)
                ? Palette.ActiveSection
                : Palette.Bright;
            host.Write($"[{section.Header}]\n", headerColor);
            foreach (var kv in section.Kvs)
            {
                host.Write($"  {kv.Key,-22}", Palette.Dim);
                host.Write($"= {kv.Value}\n", kv.Empty ? Palette.Warn : Palette.Normal);
            }
            host.Write("\n", Palette.Normal);
        }
        host.Write($"/{CommandName} <key> <value> to change  ", Palette.Dim);
        host.Write($"e.g. /{CommandName} model.provider ollama\n\n", Palette.Normal);
    }

    private static void ShowKeyList(ISlashCommandHost host)
    {
        host.Write("Available config keys:\n\n", Palette.Bright);
        var keyColumnWidth = KeyColumnWidth();
        foreach (var group in ConfigEditor.ParamList)
        {
            host.Write($"[{group.Section}]\n", Palette.Bright);
            foreach (var p in group.Params)
            {
                var key = DisplayKey(group.Section, p.Key);
                host.Write(" " + FormatKeyColumn(key, keyColumnWidth) + " ", Palette.Normal);
                host.Write($"{ConfigEditor.GetValueHint(p)}\n", Palette.Dim);
            }
            host.Write("\n", Palette.Normal);
        }
    }

    /// <summary>
    /// Returns the display width for the key column, computed as the maximum key length
    /// that falls within the upper IQR fence to avoid outliers skewing the layout. Example:
    /// kay         value1
    /// another_kay value2
    /// definitely_very_long_key_valu value3
    /// </summary>
    private static int KeyColumnWidth()
    {
        var lengths = ConfigEditor.ParamList
            .SelectMany(g => g.Params.Select(p => DisplayKey(g.Section, p.Key).Length))
            .Order()
            .ToArray();
        if (lengths.Length == 0)
            return 0;

        var q1 = Percentile(lengths, 0.25);
        var q3 = Percentile(lengths, 0.75);
        var upperFence = q3 + 1.5 * (q3 - q1);
        return lengths.Where(len => len <= upperFence).DefaultIfEmpty(lengths[0]).Max();
    }

    private static string FormatKeyColumn(string key, int width) => key.Length > width ? key : key.PadRight(width);

    private static double Percentile(int[] sorted, double percentile)
    {
        if (sorted.Length == 1)
            return sorted[0];

        var position = (sorted.Length - 1) * percentile;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper)
            return sorted[lower];

        return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }

    private static string DisplayKey(string section, string key)
    {
        var prefix = section + ".";
        return key.StartsWithNoCase(prefix)
            ? key[prefix.Length..]
            : key;
    }

    private static void SetValue(ISlashCommandHost host, DotsyConfig cfg, string args)
    {
        var spaceIdx = args.IndexOf(' ');
        if (spaceIdx < 0)
        {
            host.Write($"  usage: /{CommandName} <key> <value>  e.g. /{CommandName} model.provider ollama\n\n", Palette.Warn);
            return;
        }
        var key = args[..spaceIdx].Trim();
        var value = args[(spaceIdx + 1)..].Trim();

        // A real config key is always "section.key"; a dotless first word almost always means the
        // user typed a sentence after a stray "/config " left by autocomplete.
        if (!key.Contains('.'))
        {
            host.Write($"  '{args}' isn't a config command.\n", Palette.Warn);
            host.Write($"  To chat with the agent, send your message without the leading /{CommandName}.\n", Palette.Dim);
            host.Write($"  To change a setting, use /{CommandName} <section.key> <value> (try /{CommandName} list).\n\n", Palette.Dim);
            return;
        }

        var unchanged = ConfigEditor.CheckUnchanged(cfg, key, value);
        if (!unchanged.Ok)
        {
            host.WriteError($"Config error: {unchanged.Message}");
            return;
        }

        if (unchanged.Unchanged)
        {
            host.Write($"  {key} ", Palette.Dim);
            host.Write($"= {unchanged.CurrentValue}\n", Palette.Success);
            host.Write("  Value was unchanged.\n\n", Palette.Dim);
            return;
        }

        var (ok, msg) = ConfigEditor.Set(cfg, key, value, TuiSessionContext.ProjectConfigPath);
        if (!ok)
        {
            host.WriteError($"Config error: {msg}");
            return;
        }

        host.Write($"  {key} ", Palette.Dim);
        host.Write($"= {value}\n", Palette.Success);
        host.Write($"  Saved → {msg}\n", Palette.Dim);

        // Rebuild provider + loop whenever any model setting changes.
        if (key.StartsWithNoCase("model.") &&
            TuiSessionContext.LoopFactory is { } factory)
        {
            if (host.IsBusy)
            {
                host.Write("  (provider will reload after the current turn completes)\n", Palette.Warn);
            }
            else
            {
                try
                {
                    TuiSessionContext.Loop = factory();
                    host.Write("  Provider reloaded\n", Palette.Dim);
                }
                catch (Exception ex)
                {
                    host.WriteError($"Provider reload failed: {ex.Message}");
                }
            }
        }

        // Live re-theme: re-resolve the palette, recolor existing cells, repaint.
        if (key.StartsWithNoCase("tui.theme"))
        {
            var (resolved, fellBack) = host.ApplyTheme(value);
            if (fellBack)
                host.Write($"  Unknown theme '{value}', using dark\n", Palette.Warn);
            else if (resolved != value.Trim().ToLowerInvariant())
                host.Write($"  Resolved to {resolved}\n", Palette.Dim);
        }

        // Keep status bar in sync with model ID.
        host.SetModel(TuiSessionContext.Config.Model.ActiveModel.Id);
        host.Write("\n", Palette.Normal);
    }
}
