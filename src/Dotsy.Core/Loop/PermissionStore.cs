using System.Text.Json;
using Dotsy.Core.Config;
using Dotsy.Core.Tools;

namespace Dotsy.Core.Loop;

public sealed class PermissionStore
{
    // Permanent hard denials — cannot be overridden
    private static readonly string[] HardDenials =
    [
        "Shell(rm -rf /)",
        "Shell(rm -rf ~)",
        "Shell(rm -rf *)",
        "Shell(format *)",
        "Shell(del /f /s /q *)"
    ];

    private readonly HashSet<string> sessionAllow = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> sessionDeny = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> configAllow;
    private readonly List<string> configDeny;
    private readonly string projectPermissionsPath;
    private readonly string globalPermissionsPath;
    private readonly string cwd;
    private readonly List<PermissionDecisionRecord> recentDecisions = [];
    // Session-scoped roots (e.g. an out-of-cwd repo the user approved "for project") under which
    // Write/Edit/MultiEdit are auto-allowed without re-prompting each file.
    private readonly List<string> allowedWriteRoots = [];
    private bool allowWriteForProject;

    public bool Yolo { get; set; }

    public PermissionStore(PermissionsConfig config, string cwd)
    {
        this.cwd = cwd;
        configAllow = [.. config.AlwaysAllow];
        configDeny = [.. config.NeverAllow];
        projectPermissionsPath = Path.Combine(cwd, ".dotsy", "permissions.json");
        globalPermissionsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "dotsy", "permissions.json");

        LoadPersistedPermissions(globalPermissionsPath);
        LoadPersistedPermissions(projectPermissionsPath);
    }

    public PermissionVerdict Evaluate(string toolName, string argument)
    {
        if (Yolo)
            return PermissionVerdict.Allow;

        // Rules are matched against the raw-args key AND, for Shell, the bare command key
        // (Shell(dotnet build) rather than Shell({"command":"dotnet build","timeout_ms":...})), so
        // rules can be written against the command and are stable across timeout/other JSON fields.
        var keys = MatchKeys(toolName, argument);

        // Hard denials are absolute
        if (keys.Any(k => MatchesAny(k, HardDenials)))
            return PermissionVerdict.Deny;

        // Config deny list
        if (keys.Any(k => MatchesAny(k, configDeny) || MatchesAny(k, sessionDeny)))
            return PermissionVerdict.Deny;

        // Config and session allow lists (an explicit "always allow" for a specific path wins,
        // including for a path outside cwd the user has approved before). A wildcard allow of a
        // Shell command only applies to a single simple command (no chaining/redirection), so
        // e.g. Shell(dotnet build*) cannot authorize "dotnet build && rm -rf /".
        if (IsAllowed(toolName, argument, keys))
            return PermissionVerdict.Allow;

        // Writes outside the project directory are not covered by the project-wide allow. Prompt for
        // each one - unless the user has already approved "for project" a write under the same
        // outside root (repo), in which case sibling files there are auto-allowed. When project-wide
        // approval is active, outside-cwd writes are denied rather than prompted.
        if (IsWriteOutsideCwd(toolName, argument))
            return IsUnderAllowedWriteRoot(argument) ? PermissionVerdict.Allow
                : allowWriteForProject ? PermissionVerdict.Deny
                : PermissionVerdict.Ask;

        // Project-scoped write allowance (excludes .dotsy)
        if (allowWriteForProject && IsWriteInProjectNotDotsy(toolName, argument))
            return PermissionVerdict.Allow;

        // ReadOnly tools are always allowed
        return PermissionVerdict.Ask;
    }

    public void AllowForSession(string toolName, string argument)
    {
        var key = FormatKey(toolName, argument);
        sessionAllow.Add(key);
        TrackDecision("allow once", key);
    }

    public void AlwaysAllow(string toolName, string argument)
    {
        var key = FormatKey(toolName, argument);
        sessionAllow.Add(key);
        TrackDecision("always allow", key);
        PersistAllow(projectPermissionsPath, key);
    }

    // Grants automatic approval for Write/Edit/MultiEdit anywhere inside the project
    // folder, except .dotsy (which always prompts).
    public void AllowWriteForProject()
    {
        allowWriteForProject = true;
        TrackDecision("allow for project", "Write, Edit, and MultiEdit inside the project except .dotsy");
    }

    // When an out-of-cwd write is approved "for project", remember the project it belongs to (its
    // git repo root, or its own folder) so sibling files under it are auto-allowed for the session.
    // No-op for in-cwd writes (those are covered by AllowWriteForProject).
    public void AllowWriteRootForOutside(string toolName, string argument)
    {
        if (!IsWriteOutsideCwd(toolName, argument))
            return;
        if (ResolveWriteRoot(argument) is not { } root)
            return;
        if (!allowedWriteRoots.Contains(root, StringComparer.OrdinalIgnoreCase))
            allowedWriteRoots.Add(root);
        TrackDecision("allow for project", $"Write, Edit, and MultiEdit under {root}");
    }

    private bool IsUnderAllowedWriteRoot(string argument)
    {
        if (allowedWriteRoots.Count == 0)
            return false;
        string absPath;
        try
        {
            var path = ExtractPathArgument(argument);
            absPath = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(cwd, path));
        }
        catch { return false; }

        foreach (var root in allowedWriteRoots)
        {
            var rootSlash = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            if (absPath.StartsWith(rootSlash, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // The git repository root that contains the write target, or the file's own directory if it is
    // not inside a repo. Used as the "project" boundary for an approved out-of-cwd write.
    private string? ResolveWriteRoot(string argument)
    {
        string absPath;
        try
        {
            var path = ExtractPathArgument(argument);
            absPath = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(cwd, path));
        }
        catch { return null; }

        var dir = Path.GetDirectoryName(absPath);
        for (var probe = dir; !string.IsNullOrEmpty(probe); probe = Path.GetDirectoryName(probe))
        {
            if (Directory.Exists(Path.Combine(probe, ".git")))
                return probe;
        }
        return dir;
    }

    public void DenyForSession(string toolName, string argument)
    {
        var key = FormatKey(toolName, argument);
        sessionDeny.Add(key);
        TrackDecision("deny", key);
    }

    public PermissionStoreSnapshot Snapshot() => new(
        Yolo,
        allowWriteForProject,
        cwd,
        globalPermissionsPath,
        projectPermissionsPath,
        [.. configAllow],
        [.. configDeny],
        [.. sessionAllow],
        [.. sessionDeny],
        [.. HardDenials],
        [.. recentDecisions.DistinctBy(d => (d.Kind, d.Rule))]);

    private void LoadPersistedPermissions(string path)
    {
        if (!File.Exists(path))
            return;
        try
        {
            var json = File.ReadAllText(path);
            var doc = JsonDocument.Parse(json).RootElement;
            if (doc.TryGetProperty("always_allow", out var aa))
                foreach (var item in aa.EnumerateArray())
                    if (item.GetString() is { } s)
                        sessionAllow.Add(s);
            if (doc.TryGetProperty("never_allow", out var na))
                foreach (var item in na.EnumerateArray())
                    if (item.GetString() is { } s)
                        sessionDeny.Add(s);
        }
        catch { }
    }

    private static void PersistAllow(string path, string key)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            List<string> existing = [];
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var doc = JsonDocument.Parse(json).RootElement;
                if (doc.TryGetProperty("always_allow", out var aa))
                    foreach (var item in aa.EnumerateArray())
                        if (item.GetString() is { } s)
                            existing.Add(s);
            }

            if (!existing.Contains(key, StringComparer.OrdinalIgnoreCase))
                existing.Add(key);

            var obj = new { always_allow = existing };
            File.WriteAllText(path, JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private static string FormatKey(string toolName, string argument) =>
        $"{toolName}({argument})";

    // Keys a rule may match against. Always the raw-args key; for Shell also the bare-command key
    // (parsed out of {"command":...}) so rules match the command regardless of timeout_ms etc.
    private static List<string> MatchKeys(string toolName, string argument)
    {
        var keys = new List<string> { FormatKey(toolName, argument) };
        if (toolName.Equals(ShellTool.ToolName, StringComparison.OrdinalIgnoreCase)
            && TryGetShellCommand(argument) is { } cmd)
            keys.Add(FormatKey(toolName, cmd));
        return keys;
    }

    private bool IsAllowed(string toolName, string argument, List<string> keys)
    {
        var isShell = toolName.Equals(ShellTool.ToolName, StringComparison.OrdinalIgnoreCase);
        var command = isShell ? TryGetShellCommand(argument) : null;
        // A parsed shell command is "simple" if it can't chain, pipe, redirect, or subshell.
        var simpleShell = command is null || IsSimpleShellCommand(command);

        foreach (var pattern in configAllow.Concat(sessionAllow))
        {
            var isWildcard = pattern.Contains('*');
            foreach (var key in keys)
            {
                if (!isWildcard)
                {
                    // Exact rule: the user opted into this precise command, operators and all.
                    if (pattern.Equals(key, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                else if (MatchesGlob(key, pattern))
                {
                    // Wildcard rule: for Shell, only auto-allow a single simple command so a broad
                    // pattern like Shell(dotnet build*) can't be ridden by an appended command.
                    if (!isShell || simpleShell)
                        return true;
                }
            }
        }
        return false;
    }

    private static string? TryGetShellCommand(string argument)
    {
        if (!argument.TrimStart().StartsWith('{'))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(argument);
            if (doc.RootElement.TryGetProperty("command", out var c) && c.GetString() is { Length: > 0 } s)
                return s;
        }
        catch { }
        return null;
    }

    // Shell metacharacters that chain, pipe, redirect, background, or subshell. A command free of
    // these is a single invocation, so allowing it can't be leveraged to run something else.
    private static readonly char[] ShellControlChars = [';', '|', '&', '`', '>', '<', '\n', '\r'];

    private static bool IsSimpleShellCommand(string command) =>
        command.IndexOfAny(ShellControlChars) < 0 && !command.Contains("$(");

    private void TrackDecision(string kind, string rule)
    {
        var record = new PermissionDecisionRecord(kind, rule, DateTimeOffset.Now);
        if (!recentDecisions.Any(d => d.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase)
                && d.Rule.Equals(rule, StringComparison.OrdinalIgnoreCase)))
            recentDecisions.Add(record);
    }

    private static bool MatchesAny(string key, IEnumerable<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            if (MatchesGlob(key, pattern))
                return true;
        }
        return false;
    }

    private static bool MatchesGlob(string key, string pattern)
    {
        if (pattern.Equals(key, StringComparison.OrdinalIgnoreCase))
            return true;

        // Simple glob: * matches any sequence
        if (!pattern.Contains('*'))
            return false;

        // Convert glob to regex-style matching
        var parts = pattern.Split('*');
        int pos = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length == 0)
            {
                if (i == parts.Length - 1) return true;
                continue;
            }
            int idx = key.IndexOf(part, pos, StringComparison.OrdinalIgnoreCase);
            if (idx == -1) return false;
            if (i == 0 && idx != 0) return false;
            pos = idx + part.Length;
        }

        if (parts[^1].Length > 0 && !key.EndsWith(parts[^1], StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private bool IsWriteOutsideCwd(string toolName, string argument)
    {
        if (!toolName.Equals(WriteTool.ToolName, StringComparison.OrdinalIgnoreCase) &&
            !toolName.Equals(EditTool.ToolName, StringComparison.OrdinalIgnoreCase) &&
            !toolName.Equals(MultiEditTool.ToolName, StringComparison.OrdinalIgnoreCase))
            return false;

        var path = ExtractPathArgument(argument);
        var absPath = Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(Path.Combine(cwd, path));

        var cwdFull = Path.GetFullPath(cwd) + Path.DirectorySeparatorChar;
        return !absPath.StartsWith(cwdFull, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsWriteInProjectNotDotsy(string toolName, string argument)
    {
        if (!toolName.Equals(WriteTool.ToolName, StringComparison.OrdinalIgnoreCase) &&
            !toolName.Equals(EditTool.ToolName, StringComparison.OrdinalIgnoreCase) &&
            !toolName.Equals(MultiEditTool.ToolName, StringComparison.OrdinalIgnoreCase))
            return false;

        string absPath;
        try
        {
            var path = ExtractPathArgument(argument);
            absPath = Path.IsPathRooted(path)
                ? path
                : Path.GetFullPath(Path.Combine(cwd, path));
        }
        catch { return false; }

        var cwdFull = Path.GetFullPath(cwd);
        var cwdSlash = cwdFull + Path.DirectorySeparatorChar;
        if (!absPath.StartsWith(cwdSlash, StringComparison.OrdinalIgnoreCase))
            return false;

        var dotsySlash = Path.Combine(cwdFull, ".dotsy") + Path.DirectorySeparatorChar;
        return !absPath.StartsWith(dotsySlash, StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractPathArgument(string argument)
    {
        if (!argument.TrimStart().StartsWith('{'))
            return argument;

        try
        {
            using var doc = JsonDocument.Parse(argument);
            if (doc.RootElement.TryGetProperty("path", out var path)
                && path.GetString() is { Length: > 0 } s)
                return s;
        }
        catch { }

        return argument;
    }
}

public enum PermissionVerdict { Allow, Deny, Ask }

public sealed record PermissionStoreSnapshot(
    bool Yolo,
    bool AllowWriteForProject,
    string Cwd,
    string GlobalPermissionsPath,
    string ProjectPermissionsPath,
    IReadOnlyList<string> ConfigAllow,
    IReadOnlyList<string> ConfigDeny,
    IReadOnlyList<string> SessionAllow,
    IReadOnlyList<string> SessionDeny,
    IReadOnlyList<string> HardDenials,
    IReadOnlyList<PermissionDecisionRecord> RecentDecisions);

public sealed record PermissionDecisionRecord(string Kind, string Rule, DateTimeOffset Timestamp);
