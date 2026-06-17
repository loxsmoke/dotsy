using System.Text;
using TextRune = System.Text.Rune;

namespace Dotsy.Cli.Tui;

// Emoji that default to "emoji presentation" render as 2 columns in most terminals, but
// Terminal.Gui measures many of them as 1 column. That mismatch desyncs the fixed cell grid:
// the panel separator, scrollbar, and frame shift, and the entry-field cursor lands on the wrong
// cell. Since the app can't change the terminal's width, the only stable option is to replace
// these runes with a 1-column glyph before they enter the grid. Text-presentation symbols the app
// relies on (✓ ✗, box drawing, block/geometric scrollbar glyphs) are left untouched.
internal static class Glyphs
{
    private static readonly TextRune Placeholder = new('?');

    // Common emoji mapped to a meaning-preserving ASCII/text-presentation equivalent (each 1 column).
    private static readonly Dictionary<int, TextRune> Map = new()
    {
        [0x2705] = new('✓'), // ✅ -> ✓
        [0x274C] = new('✗'), // ❌ -> ✗
        [0x2714] = new('✓'), // ✔ -> ✓
        [0x2716] = new('✗'), // ✖ -> ✗
        [0x2611] = new('✓'), // ☑ -> ✓
        [0x26A0] = new('!'),       // ⚠
        [0x2B50] = new('*'),       // ⭐
        [0x2728] = new('*'),       // ✨
        [0x2757] = new('!'),       // ❗
        [0x2753] = new('?'),       // ❓
    };

    // ✓ (U+2713) and ✗ (U+2717) default to text presentation and are used as tool-status icons.
    private static bool IsTextPresentation(int v) => v is 0x2713 or 0x2717;

    private static bool IsEmojiPresentation(int v)
    {
        if (IsTextPresentation(v)) return false;
        return v >= 0x1F000                        // astral emoji blocks
            || (v >= 0x2600 && v <= 0x27BF)        // Misc Symbols + Dingbats (mostly emoji)
            || (v >= 0x2B00 && v <= 0x2BFF)        // Misc Symbols and Arrows (stars, etc.)
            || v is 0x2934 or 0x2935 or 0x3030 or 0x303D;
    }

    /// <summary>Returns a grid-safe (1-column) substitute for an emoji-presentation rune, else the rune.</summary>
    public static TextRune Safe(TextRune r)
    {
        if (Map.TryGetValue(r.Value, out var mapped)) return mapped;
        return IsEmojiPresentation(r.Value) ? Placeholder : r;
    }

    /// <summary>Replaces emoji-presentation runes in a string with grid-safe substitutes.</summary>
    public static string Sanitize(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var sb = new StringBuilder(text.Length);
        foreach (var r in text.EnumerateRunes())
            sb.Append(Safe(r).ToString());
        return sb.ToString();
    }
}
