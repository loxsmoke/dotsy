using Dotsy.Core.Tools;
using Dotsy.Core.Utils;
using System;
using System.Collections.Generic;
using System.Text;

namespace Dotsy.Core.Loop;

public class AgentLoopHeuristics
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
