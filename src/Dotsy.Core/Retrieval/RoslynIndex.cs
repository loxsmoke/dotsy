using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Data.Sqlite;

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
    private readonly string _dbPath;
    private SqliteConnection? _db;

    public RoslynIndex(string cacheDir)
    {
        _dbPath = Path.Combine(cacheDir, "roslyn-index.db");
        Directory.CreateDirectory(cacheDir);
    }

    public void Open()
    {
        var connString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Pooling = false
        }.ToString();
        _db = new SqliteConnection(connString);
        _db.Open();
        _db.Execute("""
            CREATE TABLE IF NOT EXISTS file_outlines (
                path TEXT PRIMARY KEY,
                last_write TEXT NOT NULL,
                outline TEXT NOT NULL,
                refs TEXT NOT NULL
            )
            """);
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

        if (_db is not null)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT outline, refs FROM file_outlines WHERE path = @p AND last_write = @lw";
            cmd.Parameters.AddWithValue("@p", filePath);
            cmd.Parameters.AddWithValue("@lw", lastWrite);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                var cachedOutline = reader.GetString(0);
                var cachedRefs = reader.GetString(1).Split('|', StringSplitOptions.RemoveEmptyEntries);
                return new FileOutline
                {
                    FilePath = filePath,
                    Outline = cachedOutline,
                    ReferencedFiles = cachedRefs,
                    LastWrite = File.GetLastWriteTimeUtc(filePath)
                };
            }
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

        if (_db is not null)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO file_outlines (path, last_write, outline, refs)
                VALUES (@p, @lw, @o, @r)
                """;
            cmd.Parameters.AddWithValue("@p", filePath);
            cmd.Parameters.AddWithValue("@lw", lastWrite);
            cmd.Parameters.AddWithValue("@o", outline);
            cmd.Parameters.AddWithValue("@r", string.Join("|", refs));
            cmd.ExecuteNonQuery();
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

    public void Dispose() => _db?.Dispose();
}

public static class SqliteExtensions
{
    public static void Execute(this SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
