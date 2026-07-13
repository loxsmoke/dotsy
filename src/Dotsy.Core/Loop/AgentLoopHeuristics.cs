using Dotsy.Core.Loop.Data;
using Dotsy.Core.Tools;
using Dotsy.Core.Utils;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Dotsy.Core.Loop;

public partial class AgentLoopHeuristics
{
    // Phrases a model uses to announce an action it is about to take.
    private static readonly string[] IntentLeads =
    [
        "let me", "let's", "let us", "i'll", "i will", "i am going to", "i'm going to",
        "i am now going to", "i'm now going to", "now i", "now let me", "next i", "next, i",
        "going to", "i'll now", "let me now", "i'll go ahead", "let me go ahead",
        "i'll proceed", "let me proceed", "proceeding to", "time to", "let me start",
    ];

    // Action verbs that indicate task work (not a closing remark) follows the intent lead.
    private static readonly string[] IntentActions =
    [
        "implement", "add", "fix", "update", "create", "write", "edit", "modify", "change",
        "start", "begin", "make", "apply", "build", "refactor", "continue", "proceed", "do",
        "handle", "wire", "set up", "read", "check", "look", "examine", "inspect",
        "investigate", "explore", "search", "find", "run", "test", "rewrite", "remove",
        "delete", "rename", "move", "replace", "correct",
    ];

    /// <summary>
    /// Observes the outcome of a build/test command and returns a corrective hint when the agent
    /// appears to be fighting stale intermediate build state rather than real source problems.
    ///
    /// The signature of that situation: several consecutive failed builds whose error message
    /// KEEPS CHANGING even though the fix for the previous error was applied. Incremental builds
    /// on a poisoned obj/ directory produce rotating phantom errors (observed dogfooding: a
    /// missing NuGet package surfaced as "Duplicate x:Class", "Ambiguous project name", and a
    /// runtime XAML failure across 46 builds — the true error only appeared after deleting
    /// obj/bin). A model that keeps fixing one error only to be shown a different one never
    /// discovers this on its own, so after three consecutive failures spanning at least two
    /// distinct error signatures the loop tells it to clean-rebuild first. A passing build
    /// resets the tracking (and re-arms the hint for a later, separate episode).
    /// </summary>
    public static string? ObserveBuildOutcome(LoopContext ctx, bool failed, string output)
    {
        if (!failed)
        {
            ctx.ConsecutiveBuildFailures = 0;
            ctx.BuildErrorSignatures.Clear();
            ctx.StaleBuildHintGiven = false;
            return null;
        }

        ctx.ConsecutiveBuildFailures++;
        var signature = BuildErrorSignature(output);
        if (signature.Length > 0 && !ctx.BuildErrorSignatures.Contains(signature))
            ctx.BuildErrorSignatures.Add(signature);

        if (ctx.StaleBuildHintGiven
            || ctx.ConsecutiveBuildFailures < 3
            || ctx.BuildErrorSignatures.Count < 2)
            return null;

        ctx.StaleBuildHintGiven = true;
        return $"This is failed build #{ctx.ConsecutiveBuildFailures} in a row, and the error has "
            + $"changed between attempts ({string.Join(" -> ", ctx.BuildErrorSignatures)}). "
            + "Build errors that keep shifting like this usually mean stale intermediate build "
            + "state, not new source problems. Before changing any more code: delete the affected "
            + "project's obj and bin folders, run a full build, and trust only the errors from "
            + "that clean build.";
    }

    /// <summary>
    /// A compact signature of a build failure: the distinct diagnostic codes in the output
    /// (CS1519, AVLN2002, MSB4025, ...), or the first line mentioning "error" when no codes are
    /// present. Used to detect that consecutive failures are DIFFERENT errors.
    /// </summary>
    public static string BuildErrorSignature(string output)
    {
        var codes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match m in DiagnosticCodeRegex().Matches(output ?? ""))
            codes.Add(m.Value);
        // NU1xxx are restore warnings that appear in both passing and failing builds — ignore
        // them unless they are all we have.
        var significant = codes.Where(c => !c.StartsWith("NU", StringComparison.Ordinal)).ToList();
        if (significant.Count > 0)
            return string.Join("+", significant);
        if (codes.Count > 0)
            return string.Join("+", codes);

        foreach (var line in (output ?? "").Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Contains("error", StringComparison.OrdinalIgnoreCase))
                return trimmed.Length <= 120 ? trimmed : trimmed[..120];
        }
        return "";
    }

    [GeneratedRegex(@"\b(?:CS|AVLN|NU|MSB|NETSDK|CA|AD)\d{3,5}\b")]
    private static partial Regex DiagnosticCodeRegex();

    /// <summary>
    /// Builds the signature used by the repeated-tool-call loop guard. For Read calls the target
    /// file's current on-disk state (mtime + size) is folded in, so re-reading a file that has
    /// CHANGED — e.g. after the agent's own edit, when the read-before-edit guard demands a fresh
    /// read — is never classified as a repeated call. Without this, the loop guard skips the
    /// mandated re-read while the edit guard keeps rejecting stale line-range edits, deadlocking
    /// the model into whole-file rewrites (observed dogfooding, webcam-sec session 20260711.1).
    /// Re-reads of an unchanged file keep an identical signature and still trip the guard.
    /// </summary>
    public static string ToolCallSignature(string cwd, string name, string args)
    {
        var signature = $"{name}:{args.Trim()}";
        if (!string.Equals(name, ReadTool.ToolName, StringComparison.Ordinal))
            return signature;

        try
        {
            var input = ToolArgs.TryParseArgs(args);
            if (input.ValueKind != System.Text.Json.JsonValueKind.Object
                || !input.TryGetProperty("path", out var pathEl)
                || pathEl.ValueKind != System.Text.Json.JsonValueKind.String)
                return signature;

            var path = pathEl.GetString();
            if (string.IsNullOrWhiteSpace(path))
                return signature;

            var full = System.IO.Path.IsPathRooted(path)
                ? path
                : System.IO.Path.GetFullPath(System.IO.Path.Combine(cwd, path));
            var fi = new System.IO.FileInfo(full);
            if (!fi.Exists)
                return signature;

            return $"{signature}@{fi.LastWriteTimeUtc.Ticks}:{fi.Length}";
        }
        catch
        {
            return signature;
        }
    }

    /// <summary>
    /// True when a text-only response reads like the model announced its next action but stopped
    /// before taking it (e.g. "Let me implement the feature now."). Only the trailing line is
    /// examined — a genuine final answer rarely *ends* by announcing more work. "Let me know…"
    /// (a closing pleasantry) is explicitly excluded.
    /// </summary>
    public static bool LooksLikeAnnouncedNextStep(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var tail = text.TrimEnd();
        var lastBreak = tail.LastIndexOfAny(['\n', '\r']);
        if (lastBreak >= 0)
            tail = tail[(lastBreak + 1)..];
        var lower = tail.Trim().ToLowerInvariant();
        if (lower.Length == 0 || lower.Contains("let me know"))
            return false;

        foreach (var lead in IntentLeads)
        {
            var idx = lower.IndexOf(lead, StringComparison.Ordinal);
            if (idx < 0)
                continue;

            var after = lower[(idx + lead.Length)..];
            // "Let me implement this:" — trailing colon is itself an announced-but-unfinished cue.
            if (after.TrimEnd().EndsWith(':'))
                return true;
            foreach (var action in IntentActions)
                if (System.Text.RegularExpressions.Regex.IsMatch(
                        after,
                        $@"\b{System.Text.RegularExpressions.Regex.Escape(action)}\b"))
                    return true;
        }

        return false;
    }

    // Second-person cues that a response is asking the user to clarify or decide, rather than acting.
    private static readonly string[] QuestionCues =
    [
        "could you", "can you", "would you", "do you want", "do you mean", "did you mean",
        "should i", "shall i", "please clarify", "please confirm", "please specify",
        "please provide", "let me know", "which ", "what's your", "what is your", "your name",
        "can you clarify", "can you confirm", "to confirm", "which one",
    ];

    /// <summary>
    /// True when a text-only response ends by asking the user to clarify/decide (contains a question
    /// mark and a second-person clarification cue). In headless runs no user can answer, so this is a
    /// dead end the loop should recover from rather than treat as completion.
    /// </summary>
    public static bool LooksLikeQuestionToUser(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.Contains('?'))
            return false;

        var tail = text.Length > 600 ? text[^600..] : text;
        var lower = tail.ToLowerInvariant();
        foreach (var cue in QuestionCues)
            if (lower.Contains(cue, StringComparison.Ordinal))
                return true;
        return false;
    }

    // Recognizes Shell commands whose success/failure is a meaningful build/verification signal
    // (compilers and test runners). Used to drive the completion guard: a Done signalled while the
    // last such command failed is refused. Deliberately narrow — a failing `ls` or `grep` should
    // not block completion.
    private static readonly string[] BuildCommandMarkers =
    [
        "dotnet build", "dotnet test", "dotnet run", "dotnet publish", "msbuild",
        "npm run build", "npm test", "npm run test", "yarn build", "yarn test",
        "pnpm build", "pnpm test", "cargo build", "cargo test", "go build", "go test",
        "make", "gradle build", "mvn ", "cmake --build",
    ];

    public static bool LooksLikeBuildCommand(string toolName, string args)
    {
        if (!string.Equals(toolName, ShellTool.ToolName, StringComparison.Ordinal))
            return false;
        var input = ToolArgs.TryParseArgs(args);
        if (!input.TryGetProperty("command", out var cmdEl) || cmdEl.GetString() is not { } cmd)
            return false;
        var lower = cmd.ToLowerInvariant();
        return BuildCommandMarkers.Any(m => lower.Contains(m, StringComparison.Ordinal));
    }

}
