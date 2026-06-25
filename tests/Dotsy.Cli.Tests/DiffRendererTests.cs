using Dotsy.Cli.Tui.Colors;
using Dotsy.Cli.Tui.Renderers;

namespace Dotsy.Cli.Tests;

[TestClass]
public sealed class DiffRendererTests
{
    [TestMethod]
    [DataRow("@@ -1,2 +1,3 @@", DiffLineKind.HunkHeader)]
    [DataRow("@@ -0,0 +1 @@ context", DiffLineKind.HunkHeader)]
    [DataRow("diff --git a/x b/x", DiffLineKind.FileHeader)]
    [DataRow("index 0123abc..4567def 100644", DiffLineKind.FileHeader)]
    [DataRow("--- a/file.cs", DiffLineKind.FileHeader)]
    [DataRow("+++ b/file.cs", DiffLineKind.FileHeader)]
    [DataRow("new file mode 100644", DiffLineKind.FileHeader)]
    [DataRow("deleted file mode 100644", DiffLineKind.FileHeader)]
    [DataRow("similarity index 95%", DiffLineKind.FileHeader)]
    [DataRow("rename from old.cs", DiffLineKind.FileHeader)]
    [DataRow("+added line", DiffLineKind.Addition)]
    [DataRow("+", DiffLineKind.Addition)]
    [DataRow("-removed line", DiffLineKind.Deletion)]
    [DataRow("-", DiffLineKind.Deletion)]
    [DataRow(" unchanged context", DiffLineKind.Context)]
    [DataRow("", DiffLineKind.Context)]
    [DataRow("no marker text", DiffLineKind.Context)]
    [DataRow("\\ No newline at end of file", DiffLineKind.NoNewline)]
    public void Classify_CategorisesLine(string line, DiffLineKind expected)
    {
        Assert.AreEqual(expected, DiffRenderer.Classify(line));
    }

    [TestMethod]
    public void Classify_FileHeaderTakesPrecedenceOverAddDelete()
    {
        // "+++"/"---" start with +/- but must classify as the file header, not an addition/deletion.
        Assert.AreEqual(DiffLineKind.FileHeader, DiffRenderer.Classify("+++ b/x"));
        Assert.AreEqual(DiffLineKind.FileHeader, DiffRenderer.Classify("--- a/x"));
    }

    [TestMethod]
    public void Color_MapsKindToThemeColor()
    {
        Assert.AreEqual(Palette.DiffHdr, DiffRenderer.Color(DiffLineKind.HunkHeader));
        Assert.AreEqual(Palette.Dim,     DiffRenderer.Color(DiffLineKind.FileHeader));
        Assert.AreEqual(Palette.Success, DiffRenderer.Color(DiffLineKind.Addition));
        Assert.AreEqual(Palette.Err,     DiffRenderer.Color(DiffLineKind.Deletion));
        Assert.AreEqual(Palette.Dim,     DiffRenderer.Color(DiffLineKind.NoNewline));
        Assert.AreEqual(Palette.DiffCtx, DiffRenderer.Color(DiffLineKind.Context));
    }
}
