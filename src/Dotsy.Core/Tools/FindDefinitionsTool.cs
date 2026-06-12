using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using Dotsy.Core.Tools.Interfaces;

namespace Dotsy.Core.Tools;

public sealed class FindDefinitionsTool : ITool
{
    private const int MaxFiles = 50;

    public string Name => "FindDefinitions";
    public string Description => "Extract C# type and member signatures from a directory. Returns a compact outline.";
    public JsonElement InputSchema => ToolSchemas.FindDefinitionsSchema;
    public ToolSafety Safety => ToolSafety.ReadOnly;
    public bool IsCompletionSignal => false;

    public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var path = ReadTool.ResolvePath(input, ctx.Cwd);

        IEnumerable<string> files;
        if (File.Exists(path))
        {
            files = [path];
        }
        else if (Directory.Exists(path))
        {
            files = Directory.GetFiles(path, "*.cs", SearchOption.AllDirectories)
                .Take(MaxFiles);
        }
        else
        {
            return Task.FromResult(ToolResult.Err($"Path not found: {path}"));
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
            var root = tree.GetCompilationUnitRoot();

            var outline = ExtractOutline(root);
            if (outline.Length > 0)
            {
                sb.AppendLine($"// {Path.GetRelativePath(path, file)}");
                sb.Append(outline);
                sb.AppendLine();
            }
            fileCount++;
        }

        if (sb.Length == 0)
            return Task.FromResult(ToolResult.Ok($"No C# definitions found in: {path}"));

        if (fileCount >= MaxFiles)
            sb.AppendLine($"<truncated: only first {MaxFiles} files shown>");

        return Task.FromResult(ToolResult.Ok(sb.ToString()));
    }

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
