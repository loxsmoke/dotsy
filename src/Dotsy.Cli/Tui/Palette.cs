using Terminal.Gui;
using TGAttribute = Terminal.Gui.Attribute;

namespace Dotsy.Cli.Tui;

// VS Code Dark+ inspired 16-colour palette
internal static class Palette
{
    static TGAttribute A(ColorName16 fg, ColorName16 bg) => new(fg, bg);

    public static readonly TGAttribute Normal   = A(ColorName16.Gray,         ColorName16.Black);
    public static readonly TGAttribute Dim      = A(ColorName16.DarkGray,     ColorName16.Black);
    public static readonly TGAttribute Bright   = A(ColorName16.White,        ColorName16.Black);
    public static readonly TGAttribute Cmd      = A(ColorName16.BrightCyan,   ColorName16.Black);
    public static readonly TGAttribute Success  = A(ColorName16.BrightGreen,  ColorName16.Black);
    public static readonly TGAttribute Err      = A(ColorName16.BrightRed,    ColorName16.Black);
    public static readonly TGAttribute Warn     = A(ColorName16.BrightYellow, ColorName16.Black);
    public static readonly TGAttribute Bullet   = A(ColorName16.BrightCyan,   ColorName16.Black);
    public static readonly TGAttribute Sub      = A(ColorName16.DarkGray,     ColorName16.Black);
    public static readonly TGAttribute DiffAdd  = A(ColorName16.BrightGreen,  ColorName16.Green);
    public static readonly TGAttribute DiffDel  = A(ColorName16.BrightRed,    ColorName16.Red);
    public static readonly TGAttribute DiffLnA  = A(ColorName16.BrightGreen,  ColorName16.Green);
    public static readonly TGAttribute DiffLnD  = A(ColorName16.BrightRed,    ColorName16.Red);
    public static readonly TGAttribute DiffHdr  = A(ColorName16.BrightCyan,   ColorName16.Black);
    public static readonly TGAttribute DiffCtx  = A(ColorName16.Gray,         ColorName16.Black);
    public static readonly TGAttribute DiffLnC  = A(ColorName16.DarkGray,     ColorName16.Black);
    public static readonly TGAttribute SelFg    = A(ColorName16.White,        ColorName16.Black);
    public static readonly TGAttribute SelHot   = A(ColorName16.BrightYellow, ColorName16.Black);
    public static readonly TGAttribute Running  = A(ColorName16.BrightYellow, ColorName16.Black);
    public static readonly TGAttribute Code      = A(ColorName16.BrightGreen,  ColorName16.Black);
    public static readonly TGAttribute SynKeyword = A(ColorName16.BrightCyan,    ColorName16.Black);
    public static readonly TGAttribute SynType    = A(ColorName16.BrightGreen,   ColorName16.Black);
    public static readonly TGAttribute SynString  = A(ColorName16.BrightYellow,  ColorName16.Black);
    public static readonly TGAttribute SynNumber  = A(ColorName16.BrightMagenta, ColorName16.Black);
    public static readonly TGAttribute StatusBg = A(ColorName16.White,        ColorName16.DarkGray);

    public static ColorScheme Scheme() => new()
    {
        Normal    = Normal,
        Focus     = SelFg,
        HotNormal = Cmd,
        HotFocus  = SelHot,
        Disabled  = Dim
    };

    public static ColorScheme FocusedPanelScheme() => new()
    {
        Normal    = Bright,
        Focus     = SelFg,
        HotNormal = Cmd,
        HotFocus  = SelHot,
        Disabled  = Dim
    };

    public static ColorScheme BtnScheme() => new()
    {
        Normal    = Bright,
        Focus     = A(ColorName16.White,        ColorName16.DarkGray),
        HotNormal = Bright,
        HotFocus  = A(ColorName16.BrightYellow, ColorName16.DarkGray),
        Disabled  = Dim
    };

    public static ColorScheme StatusScheme() => new()
    {
        Normal    = StatusBg,
        Focus     = StatusBg,
        HotNormal = A(ColorName16.BrightYellow, ColorName16.DarkGray),
        HotFocus  = A(ColorName16.BrightYellow, ColorName16.DarkGray),
        Disabled  = A(ColorName16.DarkGray,     ColorName16.DarkGray)
    };

    public static ColorScheme WarnStatusScheme() => new()
    {
        Normal    = A(ColorName16.Black,     ColorName16.BrightYellow),
        Focus     = A(ColorName16.Black,     ColorName16.BrightYellow),
        HotNormal = A(ColorName16.DarkGray,  ColorName16.BrightYellow),
        HotFocus  = A(ColorName16.DarkGray,  ColorName16.BrightYellow),
        Disabled  = A(ColorName16.DarkGray,  ColorName16.BrightYellow)
    };

    public static ColorScheme ErrStatusScheme() => new()
    {
        Normal    = A(ColorName16.White,    ColorName16.BrightRed),
        Focus     = A(ColorName16.White,    ColorName16.BrightRed),
        HotNormal = A(ColorName16.White,    ColorName16.BrightRed),
        HotFocus  = A(ColorName16.White,    ColorName16.BrightRed),
        Disabled  = A(ColorName16.DarkGray, ColorName16.BrightRed)
    };
}
