using Dotsy.Cli.Tui.Colors;
using Dotsy.Cli.Tui.Renderers;

namespace Dotsy.Cli.Tests;

using TGAttribute = Terminal.Gui.Drawing.Attribute;

[TestClass]
public sealed class SyntaxHighlighterTests
{
    [TestMethod]
    public void Highlight_CSharpClassifiesKeywordsTypesStringsNumbersAndComments()
    {
        var (segments, inBlock) = Highlight("csharp", "public class Foo { string s = \"bar\"; int n = 42; // note }");

        Assert.IsFalse(inBlock);
        AssertContains(segments, "public", Palette.SynKeyword);
        AssertContains(segments, "class", Palette.SynKeyword);
        AssertContains(segments, "Foo", Palette.SynType);
        AssertContains(segments, "string", Palette.SynKeyword);
        AssertContains(segments, "\"bar\"", Palette.SynString);
        AssertContains(segments, "int", Palette.SynKeyword);
        AssertContains(segments, "42", Palette.SynNumber);
        AssertContains(segments, "// note }", Palette.Dim);
    }

    [TestMethod]
    public void Highlight_BlockCommentContinuesAcrossLines()
    {
        var (first, firstInBlock) = Highlight("csharp", "var x = /* open");
        var (second, secondInBlock) = Highlight("csharp", "still comment */ var y = 1;", firstInBlock);

        Assert.IsTrue(firstInBlock);
        AssertContains(first, "var", Palette.SynKeyword);
        AssertContains(first, "/* open", Palette.Dim);

        Assert.IsFalse(secondInBlock);
        AssertContains(second, "still comment */", Palette.Dim);
        AssertContains(second, "var", Palette.SynKeyword);
        AssertContains(second, "1", Palette.SynNumber);
    }

    [TestMethod]
    public void Highlight_PythonUsesHashCommentsAndKeywords()
    {
        var (segments, inBlock) = Highlight("python", "def build(value): # comment");

        Assert.IsFalse(inBlock);
        AssertContains(segments, "def", Palette.SynKeyword);
        AssertContains(segments, "build", Palette.Normal);
        AssertContains(segments, "# comment", Palette.Dim);
    }

    [TestMethod]
    public void Highlight_JsonClassifiesKeysValuesNumbersAndConstants()
    {
        var (segments, inBlock) = Highlight("json", "{\"name\":\"dotsy\",\"count\":-12.5,\"ok\":true,\"none\":null}");

        Assert.IsFalse(inBlock);
        AssertContains(segments, "\"name\"", Palette.Cmd);
        AssertContains(segments, "\"dotsy\"", Palette.SynString);
        AssertContains(segments, "-12.5", Palette.SynNumber);
        AssertContains(segments, "true", Palette.SynKeyword);
        AssertContains(segments, "null", Palette.SynKeyword);
    }

    [TestMethod]
    public void Highlight_SqlKeywordMatchingIsCaseInsensitive()
    {
        var (segments, inBlock) = Highlight("sql", "select Name from Users where Id = 10 -- trailing");

        Assert.IsFalse(inBlock);
        AssertContains(segments, "select", Palette.SynKeyword);
        AssertContains(segments, "from", Palette.SynKeyword);
        AssertContains(segments, "where", Palette.SynKeyword);
        AssertContains(segments, "Name", Palette.SynType);
        AssertContains(segments, "10", Palette.SynNumber);
        AssertContains(segments, "-- trailing", Palette.Dim);
    }

    [TestMethod]
    public void Highlight_JavaScriptTemplateStringsAreStrings()
    {
        var (segments, inBlock) = Highlight("typescript", "const value = `hello ${name}`;");

        Assert.IsFalse(inBlock);
        AssertContains(segments, "const", Palette.SynKeyword);
        AssertContains(segments, "`hello ${name}`", Palette.SynString);
    }

    private static (List<Segment> Segments, bool InBlock) Highlight(
        string lang,
        string line,
        bool inBlockComment = false)
    {
        var segments = new List<Segment>();
        var nextState = SyntaxHighlighter.Highlight(
            lang,
            line,
            inBlockComment,
            (text, attr) => segments.Add(new Segment(text, attr)));
        return (segments, nextState);
    }

    private static void AssertContains(List<Segment> segments, string text, TGAttribute attr)
    {
        Assert.IsTrue(
            segments.Any(segment => segment.Text == text && segment.Attribute == attr),
            $"Expected segment '{text}' with attribute {attr}. Actual: {string.Join(" | ", segments)}");
    }

    private sealed record Segment(string Text, TGAttribute Attribute)
    {
        public override string ToString() => $"{Text} [{Attribute}]";
    }
}
