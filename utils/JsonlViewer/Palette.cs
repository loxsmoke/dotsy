using TGAttribute = Terminal.Gui.Drawing.Attribute;

internal static class Palette
{
    public static readonly TGAttribute Normal = new(ColorName16.Gray, ColorName16.Black);
    public static readonly TGAttribute Dim = new(ColorName16.DarkGray, ColorName16.Black);
    public static readonly TGAttribute Key = new(ColorName16.BrightCyan, ColorName16.Black);
    public static readonly TGAttribute String = new(ColorName16.BrightYellow, ColorName16.Black);
    public static readonly TGAttribute Number = new(ColorName16.BrightMagenta, ColorName16.Black);
    public static readonly TGAttribute Keyword = new(ColorName16.BrightGreen, ColorName16.Black);
    public static readonly TGAttribute Punctuation = new(ColorName16.White, ColorName16.Black);
    public static readonly TGAttribute Error = new(ColorName16.BrightRed, ColorName16.Black);
    public static readonly TGAttribute Selection = new(ColorName16.Black, ColorName16.BrightYellow);
    public static readonly TGAttribute Cursor = new(ColorName16.Black, ColorName16.White);
}
