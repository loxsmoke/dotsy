namespace Dotsy.Cli.Tui.Colors;

// Theme-backed color palette. Every color the UI draws is read through here, so swapping the
// active theme (at startup, or live via /config tui.theme) changes the whole UI. The public
// surface is unchanged from when these were hardcoded fields; call sites are untouched.
internal static class Palette
{
    /// <summary>The theme currently in effect; capture this before <see cref="Apply"/> to recolor.</summary>
    public static Theme ActiveTheme { get; private set; } = Themes.Dark;

    public static TGAttribute Normal     => ActiveTheme.Normal;
    public static TGAttribute Dim        => ActiveTheme.Dim;
    public static TGAttribute Bright     => ActiveTheme.Bright;
    public static TGAttribute Cmd        => ActiveTheme.Cmd;
    public static TGAttribute Success    => ActiveTheme.Success;
    public static TGAttribute Err        => ActiveTheme.Err;
    public static TGAttribute Warn       => ActiveTheme.Warn;
    public static TGAttribute Bullet     => ActiveTheme.Bullet;
    public static TGAttribute Sub        => ActiveTheme.Sub;
    public static TGAttribute DiffAdd    => ActiveTheme.DiffAdd;
    public static TGAttribute DiffDel    => ActiveTheme.DiffDel;
    public static TGAttribute DiffLnA    => ActiveTheme.DiffLnA;
    public static TGAttribute DiffLnD    => ActiveTheme.DiffLnD;
    public static TGAttribute DiffHdr    => ActiveTheme.DiffHdr;
    public static TGAttribute DiffCtx    => ActiveTheme.DiffCtx;
    public static TGAttribute DiffLnC    => ActiveTheme.DiffLnC;
    public static TGAttribute SelFg      => ActiveTheme.SelFg;
    public static TGAttribute SelHot     => ActiveTheme.SelHot;
    public static TGAttribute SelRow     => ActiveTheme.SelRow;
    public static TGAttribute Input      => ActiveTheme.Input;
    public static TGAttribute Running    => ActiveTheme.Running;
    public static TGAttribute Code       => ActiveTheme.Code;
    public static TGAttribute ActiveSection => ActiveTheme.ActiveSection;
    public static TGAttribute SynKeyword => ActiveTheme.SynKeyword;
    public static TGAttribute SynType    => ActiveTheme.SynType;
    public static TGAttribute SynString  => ActiveTheme.SynString;
    public static TGAttribute SynNumber  => ActiveTheme.SynNumber;
    public static TGAttribute StatusBg   => ActiveTheme.StatusBg;

    public static Scheme Scheme() => new()
    {
        Normal    = ActiveTheme.Normal,
        Focus     = ActiveTheme.SelFg,
        HotNormal = ActiveTheme.Cmd,
        HotFocus  = ActiveTheme.SelHot,
        Disabled  = ActiveTheme.Dim
    };

    // The focused panel's frame (border) draws in Bright. The frame title is drawn with the
    // scheme's Focus attribute while the panel has focus, so drive Focus to Bright too — that
    // keeps the title the same bright color as the surrounding frame instead of the selection color.
    public static Scheme FocusedPanelScheme() => new()
    {
        Normal    = ActiveTheme.Bright,
        Focus     = ActiveTheme.Bright,
        HotNormal = ActiveTheme.Cmd,
        HotFocus  = ActiveTheme.SelHot,
        Disabled  = ActiveTheme.Dim
    };

    // Read-only text panels (conversation, inspection). TextView draws body text AND the empty
    // background with Scheme.Focus, so Focus must carry the theme's main background; otherwise
    // the panel background takes the selection color (e.g. gray) instead of the editor background.
    // Body text uses its own baked cell attributes; Focus only colors empty space and selection.
    public static Scheme ReadOnlyTextScheme() => new()
    {
        Normal    = ActiveTheme.Normal,
        Focus     = ActiveTheme.Normal,
        HotNormal = ActiveTheme.Cmd,
        HotFocus  = ActiveTheme.SelHot,
        Disabled  = ActiveTheme.Dim,
        Active    = ActiveTheme.SelFg,
        HotActive = ActiveTheme.SelHot,
        Highlight = ActiveTheme.SelFg,
        Editable  = ActiveTheme.Normal,
        ReadOnly  = ActiveTheme.Normal,
        Code      = ActiveTheme.Code
    };

    // The command entry field. TextView draws its text with Scheme.Focus, so Input drives
    // both Normal and Focus to keep typed text the same color whether or not the field is focused.
    public static Scheme InputScheme() => new()
    {
        Normal    = ActiveTheme.Input,
        Focus     = ActiveTheme.Input,
        HotNormal = ActiveTheme.Cmd,
        HotFocus  = ActiveTheme.SelHot,
        Disabled  = ActiveTheme.Dim,
        Active    = ActiveTheme.SelFg,
        HotActive = ActiveTheme.SelHot,
        Highlight = ActiveTheme.SelFg,
        Editable  = ActiveTheme.Input,
        ReadOnly  = ActiveTheme.Input,
        Code      = ActiveTheme.Code
    };

    public static Scheme BtnScheme() => new()
    {
        Normal    = ActiveTheme.Bright,
        Focus     = ActiveTheme.BtnFocus,
        HotNormal = ActiveTheme.Bright,
        HotFocus  = ActiveTheme.BtnHotFocus,
        Disabled  = ActiveTheme.Dim
    };

    public static Scheme StatusScheme() => new()
    {
        Normal    = ActiveTheme.StatusBg,
        Focus     = ActiveTheme.StatusBg,
        HotNormal = ActiveTheme.StatusAccent,
        HotFocus  = ActiveTheme.StatusAccent,
        Disabled  = ActiveTheme.StatusDisabled
    };

    public static Scheme WarnStatusScheme() => Uniform(ActiveTheme.WarnStatus);
    public static Scheme ErrStatusScheme() => Uniform(ActiveTheme.ErrStatus);

    private static Scheme Uniform(TGAttribute attr) => new()
    {
        Normal = attr, Focus = attr, HotNormal = attr, HotFocus = attr, Disabled = attr
    };

    /// <summary>
    /// Resolves and applies a theme name. <c>system</c> resolves to dark/light via detection;
    /// unknown names fall back to dark. Returns the resolved concrete name and whether a fallback
    /// occurred (so the caller can warn).
    /// </summary>
    public static (string Resolved, bool Fellback) Apply(string? requested)
    {
        var name = (requested ?? "").Trim().ToLowerInvariant();
        ActiveTheme = Themes.ByName(name, IsSystemThemeLight());
        return (name, !Themes.Names.Contains(name));
    }

    /// <summary>
    /// Builds an attribute remapper from a previous theme to the now-active theme. An attribute is
    /// reverse-looked-up to its semantic role in <paramref name="previous"/>, then re-resolved
    /// through the active theme. Attributes not in the previous theme's set pass through unchanged.
    /// Lets already-rendered cells (scrollback, tool output, diffs) recolor on a live theme switch.
    /// </summary>
    public static Func<TGAttribute, TGAttribute> BuildRecolorMap(Theme previous)
    {
        var oldByAttr = new Dictionary<TGAttribute, string>();
        foreach (var (role, attr) in previous.AsRoleMap())
            oldByAttr.TryAdd(attr, role);   // first role wins on intra-theme collisions

        var newByRole = ActiveTheme.AsRoleMap();
        return a => oldByAttr.TryGetValue(a, out var role) && newByRole.TryGetValue(role, out var na)
            ? na
            : a;
    }

    // Best-effort terminal light/dark detection. COLORFGBG ("fg;bg") is set by several terminals;
    // a light background index (7 or 15) means light. Falls back to dark when unsupported.
    private static bool IsSystemThemeLight()
    {
        var fgbg = Environment.GetEnvironmentVariable("COLORFGBG");
        if (!string.IsNullOrWhiteSpace(fgbg))
        {
            var parts = fgbg.Split(';');
            if (parts.Length >= 2 && int.TryParse(parts[^1].Trim(), out var bg) && (bg == 7 || bg == 15))
                return true;
        }
        return false;
    }
}
