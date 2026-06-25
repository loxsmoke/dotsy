using Dotsy.Cli.Tui.Colors;
using System.Text;

namespace Dotsy.Cli.Tui.Renderers;

/// <summary>
/// Lightweight syntax highlighter for terminal display.
/// Tokenises one line at a time and emits per-segment TGAttribute colours.
/// Returns the updated in-block-comment state so the caller can persist it
/// across lines within a fenced code block.
/// </summary>
internal static class SyntaxHighlighter
{
    // ── Language configs ─────────────────────────────────────────────────────

    private sealed record LangConfig(
        HashSet<string> Keywords,
        string? LineComment,
        bool HasHashComment,
        bool HasBlockComments,
        char[] StringDelims);

    private static readonly LangConfig CSharp = new(
        new HashSet<string>(StringComparer.Ordinal)
        {
            "abstract","as","base","bool","break","byte","case","catch","char","checked",
            "class","const","continue","decimal","default","delegate","do","double","else",
            "enum","event","explicit","extern","false","finally","fixed","float","for",
            "foreach","goto","if","implicit","in","int","interface","internal","is","lock",
            "long","namespace","new","null","object","operator","out","override","params",
            "private","protected","public","readonly","ref","return","sbyte","sealed","short",
            "sizeof","stackalloc","static","string","struct","switch","this","throw","true",
            "try","typeof","uint","ulong","unchecked","unsafe","ushort","using","var","virtual",
            "void","volatile","while","async","await","dynamic","get","set","value","where",
            "yield","record","init","required","with","not","and","or","file","scoped",
            "nint","nuint","global","partial","when"
        },
        LineComment: "//", HasHashComment: false, HasBlockComments: true,
        StringDelims: ['"', '\'']);

    private static readonly LangConfig Python = new(
        new HashSet<string>(StringComparer.Ordinal)
        {
            "False","None","True","and","as","assert","async","await","break","class",
            "continue","def","del","elif","else","except","finally","for","from","global",
            "if","import","in","is","lambda","nonlocal","not","or","pass","raise","return",
            "try","while","with","yield","match","case","type","self","cls"
        },
        LineComment: null, HasHashComment: true, HasBlockComments: false,
        StringDelims: ['"', '\'']);

    private static readonly LangConfig Shell = new(
        new HashSet<string>(StringComparer.Ordinal)
        {
            "if","then","else","elif","fi","for","while","do","done","case","esac",
            "function","return","in","export","local","readonly","declare","echo","exit",
            "source","alias","unset","shift","break","continue","true","false","null",
            "cd","ls","mkdir","rm","cp","mv","cat","grep","sed","awk","find","chmod",
            "curl","wget","git","sudo","chmod","chown","ping"
        },
        LineComment: null, HasHashComment: true, HasBlockComments: false,
        StringDelims: ['"', '\'']);

    private static readonly LangConfig JavaScript = new(
        new HashSet<string>(StringComparer.Ordinal)
        {
            "break","case","catch","class","const","continue","debugger","default","delete",
            "do","else","export","extends","false","finally","for","function","if","import",
            "in","instanceof","let","new","null","return","static","super","switch","this",
            "throw","true","try","typeof","undefined","var","void","while","with","yield",
            "async","await","of","from","interface","type","enum","declare","abstract",
            "implements","namespace","module","readonly","override","as","satisfies","keyof",
            "infer","never","unknown","any"
        },
        LineComment: "//", HasHashComment: false, HasBlockComments: true,
        StringDelims: ['"', '\'', '`']);

    private static readonly LangConfig Sql = new(
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SELECT","FROM","WHERE","JOIN","LEFT","RIGHT","INNER","OUTER","ON","AS",
            "INSERT","INTO","VALUES","UPDATE","SET","DELETE","CREATE","TABLE","DROP",
            "ALTER","ADD","COLUMN","INDEX","VIEW","PROCEDURE","FUNCTION","TRIGGER",
            "AND","OR","NOT","IN","EXISTS","LIKE","IS","NULL","TRUE","FALSE",
            "GROUP","BY","ORDER","HAVING","LIMIT","OFFSET","DISTINCT","ALL","UNION",
            "WITH","CASE","WHEN","THEN","ELSE","END","BEGIN","COMMIT","ROLLBACK",
            "PRIMARY","KEY","FOREIGN","REFERENCES","UNIQUE","DEFAULT","CHECK","CONSTRAINT"
        },
        LineComment: "--", HasHashComment: false, HasBlockComments: true,
        StringDelims: ['"', '\'']);

    private static readonly LangConfig C = new(
        new HashSet<string>(StringComparer.Ordinal)
        {
            "auto","break","case","char","const","continue","default","do","double",
            "else","enum","extern","float","for","goto","if","inline","int","long",
            "register","restrict","return","short","signed","sizeof","static","struct",
            "switch","typedef","union","unsigned","void","volatile","while",
            "_Bool","_Complex","_Imaginary","_Alignas","_Alignof","_Atomic",
            "_Generic","_Noreturn","_Static_assert","_Thread_local",
            "NULL","true","false"
        },
        LineComment: "//", HasHashComment: false, HasBlockComments: true,
        StringDelims: ['"', '\'']);

    private static readonly LangConfig Cpp = new(
        new HashSet<string>(StringComparer.Ordinal)
        {
            // C base
            "auto","break","case","char","const","continue","default","do","double",
            "else","enum","extern","float","for","goto","if","inline","int","long",
            "register","restrict","return","short","signed","sizeof","static","struct",
            "switch","typedef","union","unsigned","void","volatile","while",
            "_Bool","_Complex","_Imaginary","_Alignas","_Alignof","_Atomic",
            "_Generic","_Noreturn","_Static_assert","_Thread_local","NULL",
            // C++ additions
            "alignas","alignof","and","and_eq","asm","bitand","bitor","bool",
            "catch","class","compl","concept","consteval","constexpr","constinit",
            "const_cast","co_await","co_return","co_yield","decltype","delete",
            "dynamic_cast","explicit","export","false","friend","mutable",
            "namespace","new","noexcept","not","not_eq","nullptr","operator",
            "or","or_eq","private","protected","public","reinterpret_cast",
            "requires","static_assert","static_cast","template","this",
            "thread_local","throw","true","try","typeid","typename","using",
            "virtual","wchar_t","xor","xor_eq","override","final"
        },
        LineComment: "//", HasHashComment: false, HasBlockComments: true,
        StringDelims: ['"', '\'']);

    private static readonly LangConfig Generic = new(
        [],
        LineComment: "//", HasHashComment: false, HasBlockComments: false,
        StringDelims: ['"', '\'']);

    private static LangConfig GetConfig(string lang) => lang switch
    {
        "csharp" or "cs" or "c#" or "dotnet" => CSharp,
        "c"                                  => C,
        "cpp" or "c++" or "cc" or "cxx"      => Cpp,
        "python" or "py"                     => Python,
        "bash" or "sh" or "shell" or "zsh" or "fish"  => Shell,
        "powershell" or "ps" or "ps1" or "pwsh"       => Shell,
        "javascript" or "js" or "typescript" or "ts" or "jsx" or "tsx" => JavaScript,
        "sql" or "mysql" or "postgres" or "sqlite" => Sql,
        "json" or "jsonc"                     => null!,   // dedicated path
        _                                     => Generic
    };

    // ── Public entry point ───────────────────────────────────────────────────

    /// <summary>
    /// Highlights one line. Returns the updated inBlockComment state.
    /// </summary>
    public static bool Highlight(
        string lang, string line, bool inBlockComment,
        Action<string, TGAttribute> append)
    {
        if (lang is "json" or "jsonc")
        {
            HighlightJson(line, append);
            return false;
        }
        return HighlightLine(line, GetConfig(lang), inBlockComment, append);
    }

    // ── Generic tokeniser ────────────────────────────────────────────────────

    private static bool HighlightLine(
        string line, LangConfig cfg, bool inBlockComment,
        Action<string, TGAttribute> append)
    {
        var buf = new StringBuilder();
        int i = 0;

        void FlushBuf()
        {
            if (buf.Length > 0) { append(buf.ToString(), Palette.Normal); buf.Clear(); }
        }

        // Continue a block comment started on a previous line
        if (inBlockComment)
        {
            int close = cfg.HasBlockComments
                ? line.IndexOf("*/", StringComparison.Ordinal)
                : -1;
            if (close < 0)
            {
                append(line, Palette.Dim);
                return true;
            }
            append(line[..(close + 2)], Palette.Dim);
            i = close + 2;
            inBlockComment = false;
        }

        while (i < line.Length)
        {
            char c = line[i];

            // Block comment open  /* … */
            if (cfg.HasBlockComments && c == '/' && i + 1 < line.Length && line[i + 1] == '*')
            {
                FlushBuf();
                int close = line.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (close >= 0)
                {
                    append(line[i..(close + 2)], Palette.Dim);
                    i = close + 2;
                }
                else
                {
                    append(line[i..], Palette.Dim);
                    return true;
                }
                continue;
            }

            // Hash comment  # …
            if (cfg.HasHashComment && c == '#')
            {
                FlushBuf();
                append(line[i..], Palette.Dim);
                return false;
            }

            // Line comment  // … or -- …
            if (cfg.LineComment is { } lc && i + lc.Length <= line.Length &&
                line.AsSpan(i, lc.Length).SequenceEqual(lc.AsSpan()))
            {
                FlushBuf();
                append(line[i..], Palette.Dim);
                return false;
            }

            // String literal
            if (Array.IndexOf(cfg.StringDelims, c) >= 0)
            {
                FlushBuf();
                int end = i + 1;
                while (end < line.Length)
                {
                    if (line[end] == '\\' && end + 1 < line.Length) { end += 2; continue; }
                    if (line[end] == c) { end++; break; }
                    end++;
                }
                append(line[i..end], Palette.SynString);
                i = end;
                continue;
            }

            // Number  (digits, hex, float)
            if (char.IsAsciiDigit(c))
            {
                FlushBuf();
                int end = i + 1;
                while (end < line.Length && IsNumChar(line[end])) end++;
                append(line[i..end], Palette.SynNumber);
                i = end;
                continue;
            }

            // Identifier or keyword
            if (char.IsLetter(c) || c == '_')
            {
                FlushBuf();
                int end = i;
                while (end < line.Length && (char.IsLetterOrDigit(line[end]) || line[end] == '_'))
                    end++;
                var word = line[i..end];
                TGAttribute attr;
                if (cfg.Keywords.Contains(word))
                    attr = Palette.SynKeyword;
                else if (char.IsUpper(c) && word.Length > 1)
                    attr = Palette.SynType;
                else
                    attr = Palette.Normal;
                append(word, attr);
                i = end;
                continue;
            }

            buf.Append(c);
            i++;
        }

        FlushBuf();
        return false;
    }

    private static bool IsNumChar(char c) =>
        char.IsAsciiDigit(c) || c == '.' || c == '_' || c == 'x' || c == 'X' ||
        c == 'b' || c == 'B' || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    // ── JSON highlighter ─────────────────────────────────────────────────────

    private static void HighlightJson(string line, Action<string, TGAttribute> append)
    {
        int i = 0;
        while (i < line.Length)
        {
            char c = line[i];

            if (c == '"')
            {
                int end = i + 1;
                while (end < line.Length)
                {
                    if (line[end] == '\\' && end + 1 < line.Length) { end += 2; continue; }
                    if (line[end] == '"') { end++; break; }
                    end++;
                }
                // Key if followed (after optional whitespace) by ':'
                int peek = end;
                while (peek < line.Length && line[peek] == ' ') peek++;
                bool isKey = peek < line.Length && line[peek] == ':';
                append(line[i..end], isKey ? Palette.Cmd : Palette.SynString);
                i = end;
                continue;
            }

            if (char.IsAsciiDigit(c) || (c == '-' && i + 1 < line.Length && char.IsAsciiDigit(line[i + 1])))
            {
                int end = i + 1;
                while (end < line.Length && (char.IsAsciiDigit(line[end]) || line[end] is '.' or 'e' or 'E' or '+' or '-'))
                    end++;
                append(line[i..end], Palette.SynNumber);
                i = end;
                continue;
            }

            if (char.IsLetter(c))
            {
                int end = i;
                while (end < line.Length && char.IsLetter(line[end])) end++;
                var word = line[i..end];
                append(word, word is "true" or "false" or "null" ? Palette.SynKeyword : Palette.Normal);
                i = end;
                continue;
            }

            append(c.ToString(), Palette.Normal);
            i++;
        }
    }
}
