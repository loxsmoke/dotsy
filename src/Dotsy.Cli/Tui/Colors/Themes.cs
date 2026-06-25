namespace Dotsy.Cli.Tui.Colors;

internal static class Themes
{
    private static TGAttribute A(ColorName16 fg, ColorName16 bg) => new(fg, bg);

    /// <summary>Names accepted in <c>[tui].theme</c>. <c>system</c> resolves to dark or light.</summary>
    public static string[] Names => [ Dark.Name, Light.Name, "system", Borland.Name ];

    // VS Code Dark+ inspired — the original hardcoded palette, kept as the default.
    public static readonly Theme Dark = new()
    {
        Name       = "dark",
        Normal     = A(ColorName16.Gray,         ColorName16.Black),
        Dim        = A(ColorName16.DarkGray,     ColorName16.Black),
        Bright     = A(ColorName16.White,        ColorName16.Black),
        Cmd        = A(ColorName16.BrightCyan,   ColorName16.Black),
        Success    = A(ColorName16.BrightGreen,  ColorName16.Black),
        Err        = A(ColorName16.BrightRed,    ColorName16.Black),
        Warn       = A(ColorName16.BrightYellow, ColorName16.Black),
        Bullet     = A(ColorName16.BrightCyan,   ColorName16.Black),
        Sub        = A(ColorName16.DarkGray,     ColorName16.Black),
        Running    = A(ColorName16.BrightYellow, ColorName16.Black),
        Code       = A(ColorName16.BrightGreen,  ColorName16.Black),
        ActiveSection = A(ColorName16.BrightGreen, ColorName16.Black),
        DiffAdd    = A(ColorName16.BrightGreen,  ColorName16.Green),
        DiffDel    = A(ColorName16.BrightRed,    ColorName16.Red),
        DiffLnA    = A(ColorName16.BrightGreen,  ColorName16.Green),
        DiffLnD    = A(ColorName16.BrightRed,    ColorName16.Red),
        DiffHdr    = A(ColorName16.BrightCyan,   ColorName16.Black),
        DiffCtx    = A(ColorName16.Gray,         ColorName16.Black),
        DiffLnC    = A(ColorName16.DarkGray,     ColorName16.Black),
        SelFg      = A(ColorName16.White,        ColorName16.Black),
        SelHot     = A(ColorName16.BrightYellow, ColorName16.Black),
        SelRow     = A(ColorName16.White,        ColorName16.DarkGray),
        Input      = A(ColorName16.White,        ColorName16.Black),
        SynKeyword = A(ColorName16.BrightCyan,    ColorName16.Black),
        SynType    = A(ColorName16.BrightGreen,   ColorName16.Black),
        SynString  = A(ColorName16.BrightYellow,  ColorName16.Black),
        SynNumber  = A(ColorName16.BrightMagenta, ColorName16.Black),
        StatusBg       = A(ColorName16.White,        ColorName16.DarkGray),
        StatusAccent   = A(ColorName16.BrightYellow, ColorName16.DarkGray),
        StatusDisabled = A(ColorName16.DarkGray,     ColorName16.DarkGray),
        BtnFocus       = A(ColorName16.White,        ColorName16.DarkGray),
        BtnHotFocus    = A(ColorName16.BrightYellow, ColorName16.DarkGray),
        WarnStatus     = A(ColorName16.Black,        ColorName16.BrightYellow),
        ErrStatus      = A(ColorName16.White,        ColorName16.BrightRed),
    };

    // Dark text on a light background.
    public static readonly Theme Light = new()
    {
        Name       = "light",
        Normal     = A(ColorName16.Black,    ColorName16.White),
        Dim        = A(ColorName16.DarkGray, ColorName16.White),
        Bright     = A(ColorName16.Black,    ColorName16.White),
        Cmd        = A(ColorName16.Blue,     ColorName16.White),
        Success    = A(ColorName16.Green,    ColorName16.White),
        Err        = A(ColorName16.Red,      ColorName16.White),
        Warn       = A(ColorName16.Yellow,   ColorName16.White),
        Bullet     = A(ColorName16.Blue,     ColorName16.White),
        Sub        = A(ColorName16.DarkGray, ColorName16.White),
        Running    = A(ColorName16.Yellow,   ColorName16.White),
        Code       = A(ColorName16.Green,    ColorName16.White),
        ActiveSection = A(ColorName16.Green, ColorName16.White),
        DiffAdd    = A(ColorName16.Black,    ColorName16.BrightGreen),
        DiffDel    = A(ColorName16.Black,    ColorName16.BrightRed),
        DiffLnA    = A(ColorName16.Black,    ColorName16.BrightGreen),
        DiffLnD    = A(ColorName16.Black,    ColorName16.BrightRed),
        DiffHdr    = A(ColorName16.Blue,     ColorName16.White),
        DiffCtx    = A(ColorName16.Black,    ColorName16.White),
        DiffLnC    = A(ColorName16.DarkGray, ColorName16.White),
        SelFg      = A(ColorName16.White,    ColorName16.Blue),
        SelHot     = A(ColorName16.BrightYellow, ColorName16.Blue),
        SelRow     = A(ColorName16.Black,    ColorName16.Gray),
        Input      = A(ColorName16.Black,    ColorName16.White),
        SynKeyword = A(ColorName16.Blue,     ColorName16.White),
        SynType    = A(ColorName16.Green,    ColorName16.White),
        SynString  = A(ColorName16.Red,      ColorName16.White),
        SynNumber  = A(ColorName16.Magenta,  ColorName16.White),
        StatusBg       = A(ColorName16.Black,    ColorName16.Gray),
        StatusAccent   = A(ColorName16.Blue,     ColorName16.Gray),
        StatusDisabled = A(ColorName16.DarkGray, ColorName16.Gray),
        BtnFocus       = A(ColorName16.White,    ColorName16.Blue),
        BtnHotFocus    = A(ColorName16.BrightYellow, ColorName16.Blue),
        WarnStatus     = A(ColorName16.Black,    ColorName16.BrightYellow),
        ErrStatus      = A(ColorName16.White,    ColorName16.Red),
    };

    // Turbo Pascal 7 IDE — light text on a blue background, gray status bar.
    public static readonly Theme Borland = new()
    {
        Name       = "borland",
        Normal     = A(ColorName16.Gray,         ColorName16.Blue),
        Dim        = A(ColorName16.Cyan,         ColorName16.Blue),
        Bright     = A(ColorName16.White,        ColorName16.Blue),
        Cmd        = A(ColorName16.BrightCyan,   ColorName16.Blue),
        Success    = A(ColorName16.BrightGreen,  ColorName16.Blue),
        Err        = A(ColorName16.BrightRed,    ColorName16.Blue),
        Warn       = A(ColorName16.BrightYellow, ColorName16.Blue),
        Bullet     = A(ColorName16.BrightCyan,   ColorName16.Blue),
        Sub        = A(ColorName16.Cyan,         ColorName16.Blue),
        Running    = A(ColorName16.BrightYellow, ColorName16.Blue),
        Code       = A(ColorName16.BrightGreen,  ColorName16.Blue),
        ActiveSection = A(ColorName16.BrightGreen, ColorName16.Blue),
        DiffAdd    = A(ColorName16.Black,        ColorName16.Green),
        DiffDel    = A(ColorName16.White,        ColorName16.Red),
        DiffLnA    = A(ColorName16.Black,        ColorName16.Green),
        DiffLnD    = A(ColorName16.White,        ColorName16.Red),
        DiffHdr    = A(ColorName16.BrightYellow, ColorName16.Blue),
        DiffCtx    = A(ColorName16.Gray,         ColorName16.Blue),
        DiffLnC    = A(ColorName16.Cyan,         ColorName16.Blue),
        SelFg      = A(ColorName16.Black,        ColorName16.Gray),
        SelHot     = A(ColorName16.BrightRed,    ColorName16.Gray),
        SelRow     = A(ColorName16.Black,        ColorName16.Gray),
        Input      = A(ColorName16.BrightYellow, ColorName16.Blue),
        SynKeyword = A(ColorName16.White,        ColorName16.Blue),
        SynType    = A(ColorName16.BrightCyan,   ColorName16.Blue),
        SynString  = A(ColorName16.BrightYellow, ColorName16.Blue),
        SynNumber  = A(ColorName16.BrightGreen,  ColorName16.Blue),
        StatusBg       = A(ColorName16.Black,     ColorName16.Gray),
        StatusAccent   = A(ColorName16.BrightRed, ColorName16.Gray),
        StatusDisabled = A(ColorName16.DarkGray,  ColorName16.Gray),
        BtnFocus       = A(ColorName16.Black,     ColorName16.Gray),
        BtnHotFocus    = A(ColorName16.BrightRed, ColorName16.Gray),
        WarnStatus     = A(ColorName16.Black,     ColorName16.BrightYellow),
        ErrStatus      = A(ColorName16.White,     ColorName16.BrightRed),
    };

    public static Theme ByName(string themeName, bool fallbackToLight) =>
        new List<Theme>([Light, Dark, Borland])
            .FirstOrDefault(t => t.Name == themeName) 
            is { } theme ? theme : (fallbackToLight ? Light : Dark);
}
