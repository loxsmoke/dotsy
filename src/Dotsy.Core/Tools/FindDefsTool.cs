using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using Dotsy.Core.Tools.Interfaces;
using Dotsy.Core.Utils;

namespace Dotsy.Core.Tools;

public sealed class FindDefsTool : ITool
{
    public const string ToolName = "FindDefs";
    public string Name => ToolName;
    public string Description => "Extract C# type and member signatures from a file, directory (recursive), or glob. Returns a compact outline.";
    public const int MaxFiles = 50;
    public JsonElement InputSchema => ToolSchemas.FindDefsSchema;
    public ToolSafety Safety => ToolSafety.ReadOnly;
    public bool IsCompletionSignal => false;

    public string FormatPanelArgument(JsonElement input, string cwd)
    {
        var rawPath = input.GetStringPropertyOrEmpty("path");
        if (string.IsNullOrEmpty(rawPath)) return ".";
        // A glob isn't a real path; show it verbatim rather than resolving it against cwd.
        return GlobMatcher.LooksLikeGlob(rawPath) ? rawPath : ReadTool.MakeRelative(rawPath, cwd);
    }

    public string? FormatPanelResult(JsonElement input, string resultContent, string cwd)
    {
        if (string.IsNullOrEmpty(resultContent)) return null;
        var arg = FormatPanelArgument(input, cwd);
        // Count definition lines: outline rows are indented; file headers ("// path") and notices are not.
        var defs = resultContent.Split('\n')
            .Count(l => l.StartsWith("  ") && !l.TrimStart().StartsWith("//"));
        return defs > 0 ? $"{arg}  {defs} defs" : arg;
    }

    public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var rawPath = input.GetStringPropertyOrEmpty("path");

        // Resolve `path` into a set of .cs files. The model may legitimately point at a single
        // file or a directory, but it frequently passes a glob ("**/*Foo.cs") or a bare file name
        // that doesn't exist at the resolved location; handle all of those instead of hard-failing.
        IEnumerable<string> files;
        string root; // base for relative-path display in the output

        if (GlobMatcher.LooksLikeGlob(rawPath))
        {
            var matcher = GlobMatcher.Create(rawPath);
            files = Directory.EnumerateFiles(ctx.Cwd, "*.cs", SearchOption.AllDirectories)
                .Where(f => matcher(Path.GetRelativePath(ctx.Cwd, f)))
                .Take(MaxFiles);
            root = ctx.Cwd;
        }
        else
        {
            var path = ReadTool.ResolvePath(input, ctx.Cwd);
            if (File.Exists(path))
            {
                files = [path];
                root = Path.GetDirectoryName(path) ?? path;
            }
            else if (Directory.Exists(path))
            {
                files = Directory.GetFiles(path, "*.cs", SearchOption.AllDirectories).Take(MaxFiles);
                root = path;
            }
            else
            {
                // Not an existing file/dir. If it's a bare *.cs name, search for it under the cwd
                // before giving up (the file likely lives in a subdirectory).
                var name = Path.GetFileName(path);
                var found = name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                    ? Directory.EnumerateFiles(ctx.Cwd, name, SearchOption.AllDirectories).Take(MaxFiles).ToList()
                    : [];
                if (found.Count == 0)
                    return Task.FromResult(NotFound(path, ctx.Cwd));
                files = found;
                root = ctx.Cwd;
            }
        }

        var sb = new StringBuilder();
        int fileCount = 0;

        foreach (var file in files)
        {
            if (ct.IsCancellationRequested) break;

            string source;
            try { source = File.ReadAllText(file); }
            catch { continue; }

            var tree = CSharpSyntaxTree.ParseText(source);
            var treeRoot = tree.GetCompilationUnitRoot();

            var outline = ExtractOutline(treeRoot);
            if (outline.Length > 0)
            {
                sb.AppendLine($"// {Path.GetRelativePath(root, file)}");
                sb.Append(outline);
                sb.AppendLine();
            }
            fileCount++;
        }

        if (fileCount == 0)
            return Task.FromResult(NotFound(rawPath, ctx.Cwd));

        if (sb.Length == 0)
            return Task.FromResult(ToolResult.Ok($"No C# definitions found in: {ReadTool.MakeRelative(root, ctx.Cwd)}"));

        if (fileCount >= MaxFiles)
            sb.AppendLine($"<truncated: only first {MaxFiles} files shown>");

        return Task.FromResult(ToolResult.Ok(sb.ToString()));
    }

    private static ToolResult NotFound(string path, string cwd) =>
        ToolResult.Err(
            $"No C# files found for: {ReadTool.MakeRelative(path, cwd)}\n" +
            "`path` accepts an existing .cs file, a directory to outline recursively, a glob such as " +
            "\"**/*Palette.cs\", or a bare file name to search for. To locate files first, use the Glob or Grep tools.");

    private static string ExtractOutline(CompilationUnitSyntax root)
    {
        var sb = new StringBuilder();
        foreach (var member in root.DescendantNodes().OfType<MemberDeclarationSyntax>())
        {
            switch (member)
            {
                case TypeDeclarationSyntax type:
                    var typeKind = type switch
                    {
                        ClassDeclarationSyntax => "class",
                        InterfaceDeclarationSyntax => "interface",
                        RecordDeclarationSyntax r => r.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) ? "record struct" : "record",
                        StructDeclarationSyntax => "struct",
                        _ => "type"
                    };
                    var typeModifiers = string.Join(" ", type.Modifiers.Select(m => m.Text));
                    sb.AppendLine($"  {typeModifiers} {typeKind} {type.Identifier.Text}");
                    break;

                case MethodDeclarationSyntax method when IsDirectTypeChild(method):
                    var mods = string.Join(" ", method.Modifiers.Select(m => m.Text));
                    sb.AppendLine($"    {mods} {method.ReturnType} {method.Identifier.Text}(…)");
                    break;

                case PropertyDeclarationSyntax prop when IsDirectTypeChild(prop):
                    var pMods = string.Join(" ", prop.Modifiers.Select(m => m.Text));
                    sb.AppendLine($"    {pMods} {prop.Type} {prop.Identifier.Text}");
                    break;

                case EnumDeclarationSyntax en:
                    var eMods = string.Join(" ", en.Modifiers.Select(m => m.Text));
                    sb.AppendLine($"  {eMods} enum {en.Identifier.Text}");
                    break;
            }
        }
        return sb.ToString();
    }

    private static bool IsDirectTypeChild(MemberDeclarationSyntax member) =>
        member.Parent is TypeDeclarationSyntax;
}
