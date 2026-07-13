using Dotsy.Cli.SlashCommands.Interfaces;
using Dotsy.Cli.Tui;
using Dotsy.Cli.Tui.Colors;
using Dotsy.Core.Loop.Data;

namespace Dotsy.Cli.SlashCommands;

/// <summary>
/// <c>/compact</c> — runs manual conversation compaction for the current session.
/// </summary>
internal sealed class CompactCommand : ISlashCommand
{
    public const string CommandName = "compact";
    public string Name => CommandName;

    public bool RequiresIdle => true;

    public IReadOnlyList<SlashCommandUsage> Usages =>
    [
        new($"/{Name}", "Run manual conversation compaction for the current session."),
    ];

    public void Execute(ISlashCommandHost host, string args)
    {
        var loop = TuiSessionContext.Loop;
        var loopCtx = TuiSessionContext.LoopCtx;
        var cwd = TuiSessionContext.Cwd;
        if (loop is null || loopCtx is null)
        {
            host.WriteError("session context not initialized");
            return;
        }

        var ct = host.BeginScenario();
        Task.Run(async () =>
        {
            var compacted = false;
            try
            {
                host.StartSpinner("compacting...");
                string? skipReason = null;
                await foreach (var ev in loop.CompactAsync(loopCtx, cwd, ct))
                {
                    if (ev is CompactionOccurred co)
                    {
                        compacted = true;
                        host.Invoke(() => host.Write(
                            $"\n─── compacted ({co.TokensBefore:N0}→{co.TokensAfter:N0} tokens) ───\n\n",
                            Palette.Dim));
                    }
                    else if (ev is CompactionSkipped cs)
                    {
                        skipReason = cs.Reason;
                    }
                }

                if (!compacted)
                    host.Invoke(() => host.Write(
                        skipReason is null ? "nothing to compact\n\n" : $"nothing to compact — {skipReason}\n\n",
                        Palette.Dim));
                host.UpdateStatusBarFromCtx();
            }
            catch (OperationCanceledException)
            {
                host.Invoke(() => host.Write("\n[compact cancelled]\n\n", Palette.Warn));
            }
            catch (Exception ex)
            {
                host.Invoke(() => host.WriteError($"compact failed: {ex.Message}"));
            }
            finally
            {
                host.StopSpinner("ready");
                host.EndScenario();
            }
        });
    }
}
