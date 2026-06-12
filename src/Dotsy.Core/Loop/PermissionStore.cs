using System.Text.Json;
using Dotsy.Core.Config;

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

    private readonly HashSet<string> _sessionAllow = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _sessionDeny = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _configAllow;
    private readonly List<string> _configDeny;
    private readonly string _projectPermissionsPath;
    private readonly string _globalPermissionsPath;
    private readonly string _cwd;
    private readonly List<PermissionDecisionRecord> _recentDecisions = [];
    private bool _allowWriteForProject;

    public bool Yolo { get; set; }

    public PermissionStore(PermissionsConfig config, string cwd)
    {
        _cwd = cwd;
        _configAllow = [.. config.AlwaysAllow];
        _configDeny = [.. config.NeverAllow];
        _projectPermissionsPath = Path.Combine(cwd, ".dotsy", "permissions.json");
        _globalPermissionsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "dotsy", "permissions.json");

        LoadPersistedPermissions(_globalPermissionsPath);
        LoadPersistedPermissions(_projectPermissionsPath);
    }

    public PermissionVerdict Evaluate(string toolName, string argument)
    {
        if (Yolo)
            return PermissionVerdict.Allow;

        var key = FormatKey(toolName, argument);

        // Hard denials are absolute
        if (MatchesAny(key, HardDenials))
            return PermissionVerdict.Deny;

        // Write operations outside cwd are permanently denied
        if (IsWriteOutsideCwd(toolName, argument))
            return PermissionVerdict.Deny;

        // Config deny list
        if (MatchesAny(key, _configDeny) || MatchesAny(key, _sessionDeny))
            return PermissionVerdict.Deny;

        // Config and session allow lists
        if (MatchesAny(key, _configAllow) || MatchesAny(key, _sessionAllow))
            return PermissionVerdict.Allow;

        // Project-scoped write allowance (excludes .dotsy)
        if (_allowWriteForProject && IsWriteInProjectNotDotsy(toolName, argument))
            return PermissionVerdict.Allow;

        // ReadOnly tools are always allowed
        return PermissionVerdict.Ask;
    }

    public void AllowForSession(string toolName, string argument)
    {
        var key = FormatKey(toolName, argument);
        _sessionAllow.Add(key);
        TrackDecision("allow once", key);
    }

    public void AlwaysAllow(string toolName, string argument)
    {
        var key = FormatKey(toolName, argument);
        _sessionAllow.Add(key);
        TrackDecision("always allow", key);
        PersistAllow(_projectPermissionsPath, key);
    }

    // Grants automatic approval for Write/Edit/MultiEdit anywhere inside the project
    // folder, except .dotsy (which always prompts).
    public void AllowWriteForProject()
    {
        _allowWriteForProject = true;
        TrackDecision("allow for project", "Write, Edit, and MultiEdit inside the project except .dotsy");
    }

    public void DenyForSession(string toolName, string argument)
    {
        var key = FormatKey(toolName, argument);
        _sessionDeny.Add(key);
        TrackDecision("deny", key);
    }

    public PermissionStoreSnapshot Snapshot() => new(
        Yolo,
        _allowWriteForProject,
        _cwd,
        _globalPermissionsPath,
        _projectPermissionsPath,
        [.. _configAllow],
        [.. _configDeny],
        [.. _sessionAllow],
        [.. _sessionDeny],
        [.. HardDenials],
        [.. _recentDecisions.DistinctBy(d => (d.Kind, d.Rule))]);

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
                        _sessionAllow.Add(s);
            if (doc.TryGetProperty("never_allow", out var na))
                foreach (var item in na.EnumerateArray())
                    if (item.GetString() is { } s)
                        _sessionDeny.Add(s);
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

    private void TrackDecision(string kind, string rule)
    {
        var record = new PermissionDecisionRecord(kind, rule, DateTimeOffset.Now);
        if (!_recentDecisions.Any(d => d.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase)
                && d.Rule.Equals(rule, StringComparison.OrdinalIgnoreCase)))
            _recentDecisions.Add(record);
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
        if (!toolName.Equals("Write", StringComparison.OrdinalIgnoreCase) &&
            !toolName.Equals("Edit", StringComparison.OrdinalIgnoreCase) &&
            !toolName.Equals("MultiEdit", StringComparison.OrdinalIgnoreCase))
            return false;

        var path = ExtractPathArgument(argument);
        var absPath = Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(Path.Combine(_cwd, path));

        var cwdFull = Path.GetFullPath(_cwd) + Path.DirectorySeparatorChar;
        return !absPath.StartsWith(cwdFull, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsWriteInProjectNotDotsy(string toolName, string argument)
    {
        if (!toolName.Equals("Write", StringComparison.OrdinalIgnoreCase) &&
            !toolName.Equals("Edit", StringComparison.OrdinalIgnoreCase) &&
            !toolName.Equals("MultiEdit", StringComparison.OrdinalIgnoreCase))
            return false;

        string absPath;
        try
        {
            var path = ExtractPathArgument(argument);
            absPath = Path.IsPathRooted(path)
                ? path
                : Path.GetFullPath(Path.Combine(_cwd, path));
        }
        catch { return false; }

        var cwdFull = Path.GetFullPath(_cwd);
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
