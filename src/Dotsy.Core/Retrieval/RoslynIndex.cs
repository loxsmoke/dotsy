using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Dotsy.Core.Retrieval;

public sealed class FileOutline
{
    public required string FilePath { get; init; }
    public required string Outline { get; init; }
    public required IReadOnlyList<string> ReferencedFiles { get; init; }
    public DateTimeOffset LastWrite { get; init; }
}

public sealed class RoslynIndex : IDisposable
{
    private readonly string cachePath;
    private Dictionary<string, CacheEntry>? cache;
    private bool dirty;

    private sealed class CacheEntry
    {
        public string LastWrite { get; set; } = "";
        public string Outline { get; set; } = "";
        public List<string> Refs { get; set; } = new();
    }

    public RoslynIndex(string cacheDir)
    {
        cachePath = Path.Combine(cacheDir, "roslyn-index.json");
        Directory.CreateDirectory(cacheDir);
    }

    public void Open()
    {
        if (File.Exists(cachePath))
        {
            try
            {
                var json = File.ReadAllText(cachePath);
                cache = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(json);
            }
            catch { cache = null; }
        }
        cache ??= new Dictionary<string, CacheEntry>();
    }

    public IReadOnlyList<FileOutline> ScanDirectory(string root, int maxFiles = 200)
    {
        var csFiles = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("obj") && !f.Contains("bin"))
            .Take(maxFiles)
            .ToList();

        var results = new List<FileOutline>();
        foreach (var file in csFiles)
        {
            var outline = GetOrBuild(file);
            if (outline is not null)
                results.Add(outline);
        }
        return results;
    }

    private FileOutline? GetOrBuild(string filePath)
    {
        var lastWrite = File.GetLastWriteTimeUtc(filePath).ToString("O");

        if (cache is not null
            && cache.TryGetValue(filePath, out var entry)
            && entry.LastWrite == lastWrite)
        {
            return new FileOutline
            {
                FilePath = filePath,
                Outline = entry.Outline,
                ReferencedFiles = entry.Refs,
                LastWrite = File.GetLastWriteTimeUtc(filePath)
            };
        }

        return BuildAndCache(filePath, lastWrite);
    }

    private FileOutline? BuildAndCache(string filePath, string lastWrite)
    {
        string source;
        try { source = File.ReadAllText(filePath); }
        catch { return null; }

        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetCompilationUnitRoot();

        var outline = BuildOutline(root, filePath);
        var refs = ExtractTypeReferences(root);

        if (cache is not null)
        {
            cache[filePath] = new CacheEntry
            {
                LastWrite = lastWrite,
                Outline = outline,
                Refs = refs
            };
            dirty = true;
        }

        return new FileOutline
        {
            FilePath = filePath,
            Outline = outline,
            ReferencedFiles = refs,
            LastWrite = File.GetLastWriteTimeUtc(filePath)
        };
    }

    private static string BuildOutline(CompilationUnitSyntax root, string filePath)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"// {Path.GetFileName(filePath)}");

        foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            var kind = type switch
            {
                ClassDeclarationSyntax => "class",
                InterfaceDeclarationSyntax => "interface",
                RecordDeclarationSyntax r => r.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) ? "record struct" : "record",
                StructDeclarationSyntax => "struct",
                _ => "type"
            };
            var mods = string.Join(" ", type.Modifiers.Select(m => m.Text));
            sb.AppendLine($"  {mods} {kind} {type.Identifier.Text}");

            foreach (var member in type.Members)
            {
                switch (member)
                {
                    case MethodDeclarationSyntax method:
                        var mMods = string.Join(" ", method.Modifiers.Select(m => m.Text));
                        sb.AppendLine($"    {mMods} {method.ReturnType} {method.Identifier.Text}(…)");
                        break;
                    case PropertyDeclarationSyntax prop:
                        var pMods = string.Join(" ", prop.Modifiers.Select(m => m.Text));
                        sb.AppendLine($"    {pMods} {prop.Type} {prop.Identifier.Text}");
                        break;
                }
            }
        }

        return sb.ToString();
    }

    private static List<string> ExtractTypeReferences(CompilationUnitSyntax root)
    {
        var refs = new HashSet<string>();
        foreach (var id in root.DescendantNodes().OfType<IdentifierNameSyntax>())
            refs.Add(id.Identifier.Text);
        return refs.Take(100).ToList();
    }

    public void Dispose()
    {
        if (!dirty || cache is null)
            return;

        try
        {
            var json = JsonSerializer.Serialize(cache);
            File.WriteAllText(cachePath, json);
        }
        catch { }
    }
}
