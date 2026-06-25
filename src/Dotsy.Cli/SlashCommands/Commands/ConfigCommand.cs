using Dotsy.Cli.SlashCommands.Interfaces;
using Dotsy.Cli.Tui;
using Dotsy.Cli.Tui.Colors;
using Dotsy.Core.Config;

namespace Dotsy.Cli.SlashCommands;

/// <summary>
/// <c>/config</c> — views the active config, lists settable keys, or sets a key. Setting a
/// <c>model.*</c> key reloads the provider; setting <c>tui.theme</c> re-themes live.
/// </summary>
internal sealed class ConfigCommand : ISlashCommand
{
    public string Name => "config";

    public IReadOnlyList<SlashCommandUsage> Usages =>
    [
        new("/config", "Show the active config file path and current configuration values grouped by section."),
        new("/config list", "List every settable configuration key, type, and description."),
        new("/config <key> <value>", "Update a config value via ConfigEditor.Set."),
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
            var headerColor = section.Header.Equals(activeSection, StringComparison.OrdinalIgnoreCase)
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
        host.Write("  /config <key> <value> to change  ", Palette.Dim);
        host.Write("e.g. /config model.anthropic.id claude-opus-4-7\n\n", Palette.Normal);
    }

    private static void ShowKeyList(ISlashCommandHost host)
    {
        host.Write("Available config keys:\n\n", Palette.Bright);
        foreach (var group in ConfigEditor.ParamList)
        {
            host.Write($"[{group.Section}]\n", Palette.Bright);
            foreach (var p in group.Params)
            {
                host.Write($"  {p.Key,-36}", Palette.Normal);
                host.Write($"{p.Type,-8}", Palette.Dim);
                host.Write($"{p.Description}\n", Palette.Dim);
            }
            host.Write("\n", Palette.Normal);
        }
    }

    private static void SetValue(ISlashCommandHost host, DotsyConfig cfg, string args)
    {
        var spaceIdx = args.IndexOf(' ');
        if (spaceIdx < 0)
        {
            host.Write("  usage: /config <key> <value>  e.g. /config model.provider anthropic\n\n", Palette.Warn);
            return;
        }
        var key = args[..spaceIdx].Trim();
        var value = args[(spaceIdx + 1)..].Trim();

        // A real config key is always "section.key"; a dotless first word almost always means the
        // user typed a sentence after a stray "/config " left by autocomplete.
        if (!key.Contains('.'))
        {
            host.Write($"  '{args}' isn't a config command.\n", Palette.Warn);
            host.Write("  To chat with the agent, send your message without the leading /config.\n", Palette.Dim);
            host.Write("  To change a setting, use /config <section.key> <value> (try /config list).\n\n", Palette.Dim);
            return;
        }

        var (ok, msg) = ConfigEditor.Set(cfg, key, value, TuiSessionContext.ProjectConfigPath);
        if (!ok)
        {
            host.WriteError($"config error: {msg}");
            return;
        }

        host.Write($"  {key} ", Palette.Dim);
        host.Write($"= {value}\n", Palette.Success);
        host.Write($"  saved → {msg}\n", Palette.Dim);

        // Rebuild provider + loop whenever any model setting changes.
        if (key.StartsWith("model.", StringComparison.OrdinalIgnoreCase) &&
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
                    host.Write("  provider reloaded\n", Palette.Dim);
                }
                catch (Exception ex)
                {
                    host.WriteError($"provider reload failed: {ex.Message}");
                }
            }
        }

        // Live re-theme: re-resolve the palette, recolor existing cells, repaint.
        if (key.Equals("tui.theme", StringComparison.OrdinalIgnoreCase))
        {
            var (resolved, fellBack) = host.ApplyTheme(value);
            if (fellBack)
                host.Write($"  unknown theme '{value}', using dark\n", Palette.Warn);
            else if (resolved != value.Trim().ToLowerInvariant())
                host.Write($"  resolved to {resolved}\n", Palette.Dim);
        }

        // Keep status bar in sync with model ID.
        host.SetModel(TuiSessionContext.Config.Model.ActiveModelId);
        host.Write("\n", Palette.Normal);
    }
}
