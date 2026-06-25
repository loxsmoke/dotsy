using System.Collections.Concurrent;
using System.Text;
using Dotsy.Core.Config;
using Dotsy.Core.Loop.Data;
using Dotsy.Core.Providers;
using Dotsy.Core.Tools;

namespace Dotsy.Core.Loop;

public sealed class AgentSubTaskManager
{
    private static readonly TimeSpan CompletedRetention = TimeSpan.FromMinutes(15);
    private readonly ConcurrentDictionary<string, SubTaskState> tasks = new(StringComparer.Ordinal);
    private readonly Func<IProvider> providerFactory;
    private readonly ToolRegistry toolRegistry;
    private readonly PermissionStore permissions;
    private readonly DotsyConfig config;
    private readonly string cwd;

    public AgentSubTaskManager(
        Func<IProvider> providerFactory,
        ToolRegistry registry,
        PermissionStore permissions,
        DotsyConfig config,
        string cwd)
    {
        this.providerFactory = providerFactory;
        toolRegistry = registry;
        this.permissions = permissions;
        this.config = config;
        this.cwd = cwd;
    }

    public Task<string> LaunchAsync(string description, string prompt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Cleanup();

        var id = $"task-{Guid.NewGuid():N}"[..18];
        var state = new SubTaskState(id, description, prompt);
        if (!tasks.TryAdd(id, state))
            throw new InvalidOperationException($"Could not register sub-task {id}");

        state.RunningTask = Task.Run(() => RunSubTaskAsync(state), CancellationToken.None);
        return Task.FromResult(id);
    }

    public Task<string> GetStatusAsync(string taskId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Cleanup();

        if (!tasks.TryGetValue(taskId, out var state))
            return Task.FromResult($"task_id={taskId}\nstatus=not_found");

        var sb = new StringBuilder();
        sb.AppendLine($"task_id={state.Id}");
        sb.AppendLine($"status={state.Status}");
        if (!string.IsNullOrWhiteSpace(state.Description))
            sb.AppendLine($"description={state.Description}");
        if (state.EndReason is not null)
            sb.AppendLine($"end_reason={state.EndReason}");
        if (!string.IsNullOrWhiteSpace(state.Error))
            sb.AppendLine($"error={state.Error}");
        if (!string.IsNullOrWhiteSpace(state.Result))
        {
            sb.AppendLine("result:");
            sb.AppendLine(state.Result.TrimEnd());
        }

        return Task.FromResult(sb.ToString().TrimEnd());
    }

    private async Task RunSubTaskAsync(SubTaskState state)
    {
        try
        {
            state.Status = "running";
            var provider = providerFactory();
            var ctx = new LoopContext();
            var modelInfo = await provider.GetModelInfoAsync(config.Model.ActiveModelId, CancellationToken.None);
            ctx.TokenBudget = new TokenBudget(
                modelInfo.ContextWindow,
                config.Compaction.ReserveTokens,
                config.Compaction.KeepRecentTokens,
                0);
            ctx.Messages.Add(new UserMessage([new TextBlock(state.Prompt)]));

            const string SubTaskSystemPrompt =
                "You are a background sub-agent. Work independently on the delegated prompt. "
                + "Use tools when needed, then provide a concise final result.";
            var loop = new AgentLoop(provider, toolRegistry, permissions, config, SubTaskSystemPrompt);
            var result = new StringBuilder();

            await foreach (var ev in loop.RunAsync(ctx, cwd, CancellationToken.None))
            {
                switch (ev)
                {
                    case TextChunk text:
                        result.Append(text.Text);
                        break;
                    case LoopEnded ended:
                        state.EndReason = ended.Reason.ToString();
                        if (ended.Reason == EndReason.Error || ended.Reason == EndReason.ContextTooSmall)
                            state.Error = ended.Message ?? ended.Reason.ToString();
                        break;
                }
            }

            state.Result = result.ToString().Trim();
            state.Status = state.Error is null ? "completed" : "failed";
        }
        catch (Exception ex)
        {
            state.Error = ex.Message;
            state.Status = "failed";
        }
        finally
        {
            state.CompletedAt = DateTimeOffset.UtcNow;
        }
    }

    private void Cleanup()
    {
        var cutoff = DateTimeOffset.UtcNow - CompletedRetention;
        foreach (var pair in tasks)
        {
            if (pair.Value.CompletedAt is { } completedAt && completedAt < cutoff)
                tasks.TryRemove(pair.Key, out _);
        }
    }

    private sealed class SubTaskState
    {
        public SubTaskState(string id, string description, string prompt)
        {
            Id = id;
            Description = description;
            Prompt = prompt;
        }

        public string Id { get; }
        public string Description { get; }
        public string Prompt { get; }
        public string Status { get; set; } = "queued";
        public string? Result { get; set; }
        public string? Error { get; set; }
        public string? EndReason { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public Task? RunningTask { get; set; }
    }
}
