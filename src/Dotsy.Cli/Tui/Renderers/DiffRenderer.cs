using Dotsy.Cli.Tui.Colors;

namespace Dotsy.Cli.Tui.Renderers;

// Classification of a single unified-diff line, decoupled from colors so it can be unit-tested.
// Public only so the (public) test methods can take it as a parameter.
public enum DiffLineKind
{
    HunkHeader,  // @@ -a,b +c,d @@
    FileHeader,  // diff --git, index, ---, +++, new file, deleted file, similarity, rename
    Addition,    // +...
    Deletion,    // -...
    NoNewline,   // \ No newline at end of file
    Context      // unchanged line (leading space) or anything else
}

// Pure helpers for rendering a unified-diff patch. Classify is a string→kind function (no UI
// dependency) so the categorisation rules can be unit-tested; Color maps a kind to a theme color.
internal static class DiffRenderer
{
    public static DiffLineKind Classify(string line)
    {
        // File-header prefixes must be checked before +/- because "+++"/"---" also start with +/-.
        if (line.StartsWith("@@", StringComparison.Ordinal))
            return DiffLineKind.HunkHeader;
        if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal)
            || line.StartsWith("diff ", StringComparison.Ordinal) || line.StartsWith("index ", StringComparison.Ordinal)
            || line.StartsWith("new file", StringComparison.Ordinal) || line.StartsWith("deleted file", StringComparison.Ordinal)
            || line.StartsWith("similarity", StringComparison.Ordinal) || line.StartsWith("rename ", StringComparison.Ordinal))
            return DiffLineKind.FileHeader;
        if (line.StartsWith('\\'))
            return DiffLineKind.NoNewline;
        if (line.StartsWith('+'))
            return DiffLineKind.Addition;
        if (line.StartsWith('-'))
            return DiffLineKind.Deletion;
        return DiffLineKind.Context;
    }

    public static TGAttribute Color(DiffLineKind kind) => kind switch
    {
        DiffLineKind.HunkHeader => Palette.DiffHdr,
        DiffLineKind.FileHeader => Palette.Dim,
        DiffLineKind.Addition   => Palette.Success,
        DiffLineKind.Deletion   => Palette.Err,
        DiffLineKind.NoNewline  => Palette.Dim,
        _                       => Palette.DiffCtx,
    };
}
