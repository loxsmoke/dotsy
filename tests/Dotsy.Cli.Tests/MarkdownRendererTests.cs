using Dotsy.Cli.Tui.Colors;
using Dotsy.Cli.Tui.Renderers;

namespace Dotsy.Cli.Tests;

using TGAttribute = Terminal.Gui.Drawing.Attribute;

[TestClass]
public sealed class MarkdownRendererTests
{
    [TestMethod]
    public void Write_RendersHeadingWithoutMarkdownPrefix()
    {
        var segments = Render("# Heading\n");

        AssertContains(segments, "Heading", Palette.Bright);
        AssertContains(segments, "\n", Palette.Normal);
        Assert.AreEqual("Heading\n", PlainText(segments));
    }

    [TestMethod]
    public void Write_RendersListsAndHorizontalRules()
    {
        var segments = Render("- item\n12. numbered\n---\n", wrapWidth: 6);

        AssertContains(segments, "  \u2022 ", Palette.Bullet);
        AssertContains(segments, "item", Palette.Normal);
        AssertContains(segments, "  12. ", Palette.Bullet);
        AssertContains(segments, "numbered", Palette.Normal);
        AssertContains(segments, new string('\u2500', 6), Palette.Dim);
    }

    [TestMethod]
    public void Write_RendersBlockquoteAndIndentedCode()
    {
        var segments = Render("> quoted\n    var x = 1;\n");

        AssertContains(segments, "\u2502 ", Palette.Dim);
        AssertContains(segments, "quoted", Palette.Dim);
        AssertContains(segments, "  var x = 1;", Palette.Code);
    }

    [TestMethod]
    public void Write_RendersInlineMarkup()
    {
        var segments = Render("plain [label](https://example.test) `code` ~~old~~ **bold** *em*\n");

        AssertContains(segments, "plain ", Palette.Normal);
        AssertContains(segments, "label", Palette.Cmd);
        AssertContains(segments, "code", Palette.Code);
        AssertContains(segments, "old", Palette.Dim);
        AssertContains(segments, "bold", Palette.Bright);
        AssertContains(segments, "em", Palette.Bright);
        Assert.IsFalse(PlainText(segments).Contains("https://example.test"));
    }

    [TestMethod]
    public void Write_CodeFenceDelegatesToSyntaxHighlighter()
    {
        var segments = Render("```csharp\npublic class Foo\n```\n");

        AssertContains(segments, "  ", Palette.Normal);
        AssertContains(segments, "public", Palette.SynKeyword);
        AssertContains(segments, "class", Palette.SynKeyword);
        AssertContains(segments, "Foo", Palette.SynType);
        Assert.AreEqual(3, segments.Count(segment => segment.Text == "\n"));
    }

    [TestMethod]
    public void Flush_CommitsTrailingBufferedTextWithoutNewline()
    {
        var segments = Render("trailing **text**", flush: true);

        AssertContains(segments, "trailing ", Palette.Normal);
        AssertContains(segments, "text", Palette.Bright);
        Assert.IsFalse(segments.Any(segment => segment.Text == "\n"));
    }

    private static List<Segment> Render(string markdown, int wrapWidth = 80, bool flush = false)
    {
        var segments = new List<Segment>();
        var renderer = new MarkdownRenderer(
            wrapWidth,
            (text, attr) => segments.Add(new Segment(text, attr)));
        renderer.Write(markdown);
        if (flush)
            renderer.Flush();
        return segments;
    }

    private static string PlainText(IEnumerable<Segment> segments) =>
        string.Concat(segments.Select(segment => segment.Text));

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
