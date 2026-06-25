using Dotsy.Core.Session.Data;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dotsy.Core.Session;

public sealed class SessionStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private readonly string sessionId;
    private readonly string jsonlPath;
    private readonly string indexPath;
    private readonly bool disabled;
    private string? lastParentUuid;
    private int messageCount;

    public string SessionId => sessionId;

    public SessionStore(string sessionId, string sessionDir, bool disabled = false)
    {
        this.sessionId = sessionId;
        this.disabled = disabled || Environment.GetEnvironmentVariable("DOTSY_NO_HISTORY") == "1";

        if (this.disabled) { jsonlPath = ""; indexPath = ""; return; }

        Directory.CreateDirectory(sessionDir);
        jsonlPath = Path.Combine(sessionDir, $"{sessionId}.jsonl");
        indexPath = Path.Combine(Path.GetDirectoryName(sessionDir)!, "sessions.json");
    }

    /// <summary>
    /// Returns the next session ID in YYYYMMDD.N format based on existing files in sessionDir.
    /// </summary>
    public static string NextId(string sessionDir)
    {
        var today = DateTime.Now.ToString("yyyyMMdd");
        int max = 0;
        if (Directory.Exists(sessionDir))
        {
            foreach (var f in Directory.GetFiles(sessionDir, $"{today}.*.jsonl"))
            {
                var name = Path.GetFileNameWithoutExtension(f);
                var dot = name.LastIndexOf('.');
                if (dot >= 0 && int.TryParse(name[(dot + 1)..], out var n))
                    max = Math.Max(max, n);
            }
        }
        return $"{today}.{max + 1}";
    }

    /// <summary>Resolves the absolute session directory from a (possibly relative) logDir and cwd.</summary>
    public static string ResolveDir(string logDir, string currentDirectory) =>
        Path.IsPathRooted(logDir) ? logDir : Path.Combine(currentDirectory, logDir);

    public void Append(SessionRecord record)
    {
        if (disabled) return;
        record.SessionId = sessionId;
        if (lastParentUuid is not null)
            record.ParentUuid = lastParentUuid;

        lastParentUuid = record.Uuid;
        messageCount++;

        var line = JsonSerializer.Serialize(record, JsonOpts);
        File.AppendAllText(jsonlPath, line + "\n");
    }

    public void UpdateIndex(string title, string cwd, string model)
    {
        if (disabled) return;
        try
        {
            var entries = LoadIndex(indexPath);
            var existing = entries.FirstOrDefault(e => e.SessionId == sessionId);
            if (existing is null)
            {
                existing = new SessionIndexEntry
                {
                    SessionId = sessionId,
                    Title = title,
                    Cwd = cwd,
                    Model = model,
                    CreatedAt = DateTimeOffset.UtcNow
                };
                entries.Add(existing);
            }
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            existing.MessageCount = messageCount;
            if (!string.IsNullOrEmpty(title)) existing.Title = title;

            SaveIndex(indexPath, entries);
        }
        catch { }
    }

    public static IReadOnlyList<SessionIndexEntry> GetAllSessions(string sessionDir, string? cwdFilter = null)
    {
        var indexPath = Path.Combine(Path.GetDirectoryName(sessionDir)!, "sessions.json");
        var entries = LoadIndex(indexPath);
        if (cwdFilter is not null)
            entries = entries.Where(e => e.Cwd == cwdFilter).ToList();
        return entries.OrderByDescending(e => e.UpdatedAt).ToList();
    }

    public static void CleanOldSessions(string sessionDir, int olderThanDays)
    {
        if (olderThanDays <= 0) return;
        var indexPath = Path.Combine(Path.GetDirectoryName(sessionDir)!, "sessions.json");
        var cutoff = DateTimeOffset.UtcNow.AddDays(-olderThanDays);
        var entries = LoadIndex(indexPath);
        var removed = entries.Where(e => e.UpdatedAt < cutoff).ToList();

        foreach (var entry in removed)
        {
            try
            {
                var file = Path.Combine(sessionDir, $"{entry.SessionId}.jsonl");
                if (File.Exists(file)) File.Delete(file);
            }
            catch { }
        }

        SaveIndex(indexPath, entries.Where(e => e.UpdatedAt >= cutoff).ToList());
    }

    private static List<SessionIndexEntry> LoadIndex(string indexPath)
    {
        if (!File.Exists(indexPath)) return [];
        try
        {
            var json = File.ReadAllText(indexPath);
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("sessions", out var sessions))
                return JsonSerializer.Deserialize<List<SessionIndexEntry>>(sessions.GetRawText(), JsonOpts) ?? [];
        }
        catch { }
        return [];
    }

    private static void SaveIndex(string indexPath, List<SessionIndexEntry> entries)
    {
        var dir = Path.GetDirectoryName(indexPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(indexPath, JsonSerializer.Serialize(
            new { sessions = entries },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));
    }
}
