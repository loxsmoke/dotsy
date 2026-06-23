using Dotsy.Cli.SlashCommands.Interfaces;
using Dotsy.Cli.Tui;
using Dotsy.Core.Config;
using Dotsy.Core.Providers;

namespace Dotsy.Cli.SlashCommands;

/// <summary>
/// <c>/model</c> — shows provider/model/API-key/context info, or switches the active model id.
/// </summary>
internal sealed class ModelCommand : ISlashCommand
{
    public string Name => "model";

    public IReadOnlyList<SlashCommandUsage> Usages =>
    [
        new("/model", "Show current provider, model ID, and API-key source."),
        new("/model <id>", "Switch the in-memory model ID for the active session and update the status bar."),
    ];

    public void Execute(ISlashCommandHost host, string args)
    {
        // If a model id is specified, switch the active model.
        if (!string.IsNullOrEmpty(args))
        {
            TuiSessionContext.Config.Model.ActiveModelId = args;
            host.SetModel(args);
            host.Write($"model → {args}\n\n", Palette.Success);
            return;
        }

        var cfg = TuiSessionContext.Config;
        var provider = ConfigLoader.GetProviderDisplayName(cfg.Model.Provider);
        var keySource = ConfigLoader.GetApiKeySource(cfg);
        var activeId = cfg.Model.ActiveModelId;
        host.Write("  Provider:  ", Palette.Dim); host.Write($"{provider}\n", Palette.Normal);
        host.Write("  Model:     ", Palette.Dim); host.Write($"{activeId}\n", Palette.Bright);

        // Fetch context-window info OFF the UI thread. The lookup awaits an HTTP call whose
        // continuation is posted back to the Terminal.Gui main loop, so blocking on .Result here
        // would deadlock the UI thread. Append the remaining lines once the lookup resolves.
        var lookup = TuiSessionContext.ModelInfoLookup;
        _ = Task.Run(async () =>
        {
            ModelInfo? info = null;
            var failed = false;
            try
            {
                if (lookup != null)
                    info = await lookup(activeId).ConfigureAwait(false);
            }
            catch { failed = true; }

            host.Invoke(() =>
            {
                var context = "unknown";
                if (failed)
                {
                    context = "error fetching";
                }
                else if (info != null)
                {
                    context = info.ContextWindow.ToString();
                    // Advertised = the model's capability ceiling, not the active runtime window
                    // (e.g. an unloaded Ollama model whose loaded num_ctx isn't known yet).
                    if (info.Source == ModelInfoSource.Advertised)
                        context += " (advertised)";
                }

                host.Write("  Context:   ", Palette.Dim); host.Write($"{context}\n", Palette.Bright);
                host.Write("  Api key:   ", Palette.Dim);
                if (keySource == ConfigLoader.NoKeyRequired)
                    host.Write($"{ConfigLoader.NoKeyRequired}\n\n", Palette.Bright);
                else if (keySource == ConfigLoader.KeyNotSpecified)
                    host.Write($"{ConfigLoader.KeyNotSpecified}\n\n", Palette.Bright);
                else
                    host.Write($"specified via {keySource}\n\n", Palette.Bright);
            });
        });
    }
}
