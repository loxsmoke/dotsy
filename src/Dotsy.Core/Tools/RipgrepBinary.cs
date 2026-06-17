using Ivy.Ripgrep.Bootstrap;

namespace Dotsy.Core.Tools;

/// <summary>
/// Locates an already-installed ripgrep binary so Grep never has to download one silently.
/// Resolution order: DOTSY_RIPGREP_PATH env var, then PATH, then a binary previously
/// provisioned by Ivy.Ripgrep into the local app-data cache. Returns null if none is found —
/// callers must ask the user before falling back to a network download.
/// </summary>
internal static class RipgrepBinary
{
    public static string? FindLocal()
    {
        var overridePath = Environment.GetEnvironmentVariable("DOTSY_RIPGREP_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            return overridePath;

        var onPath = FindOnPath();
        if (onPath is not null)
            return onPath;

        return FindInIvyCache();
    }

    private static string ExeName => OperatingSystem.IsWindows() ? "rg.exe" : "rg";

    private static string? FindOnPath()
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
                continue;
            string candidate;
            try { candidate = Path.Combine(dir.Trim(), ExeName); }
            catch { continue; }
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    // Reuse a binary Ivy.Ripgrep already cached (e.g. %LOCALAPPDATA%/Ivy.Ripgrep/bin/<ver>/<target>/rg).
    private static string? FindInIvyCache()
    {
        try
        {
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(baseDir))
                return null;
            var root = Path.Combine(baseDir, "Ivy.Ripgrep", "bin");
            if (!Directory.Exists(root))
                return null;
            return Directory.EnumerateFiles(root, ExeName, SearchOption.AllDirectories).FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Binary provider that returns a fixed, already-resolved ripgrep path (never downloads).</summary>
internal sealed class FixedRipgrepBinaryProvider(string path) : IRipgrepBinaryProvider
{
    public Task<string> GetBinaryPathAsync(string? requiredVersion, CancellationToken ct) =>
        Task.FromResult(path);
}
