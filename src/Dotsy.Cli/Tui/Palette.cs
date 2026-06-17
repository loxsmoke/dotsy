using Terminal.Gui;
using TGAttribute = Terminal.Gui.Attribute;

namespace Dotsy.Cli.Tui;

// Theme-backed color palette. Every color the UI draws is read through here, so swapping the
// active theme (at startup, or live via /config tui.theme) changes the whole UI. The public
// surface is unchanged from when these were hardcoded fields — call sites are untouched.
internal static class Palette
{
    private static Theme _active = Themes.Dark;

    /// <summary>The theme name actually in effect (a concrete theme: dark | light | borland).</summary>
    public static string ActiveName { get; private set; } = "dark";

    /// <summary>The raw value requested in config (e.g. "system"), before resolution.</summary>
    public static string RequestedName { get; private set; } = "dark";

    /// <summary>The theme currently in effect — capture this before <see cref="Apply"/> to recolor.</summary>
    public static Theme ActiveTheme => _active;

    public static TGAttribute Normal     => _active.Normal;
    public static TGAttribute Dim        => _active.Dim;
    public static TGAttribute Bright     => _active.Bright;
    public static TGAttribute Cmd        => _active.Cmd;
    public static TGAttribute Success    => _active.Success;
    public static TGAttribute Err        => _active.Err;
    public static TGAttribute Warn       => _active.Warn;
    public static TGAttribute Bullet     => _active.Bullet;
    public static TGAttribute Sub        => _active.Sub;
    public static TGAttribute DiffAdd    => _active.DiffAdd;
    public static TGAttribute DiffDel    => _active.DiffDel;
    public static TGAttribute DiffLnA    => _active.DiffLnA;
    public static TGAttribute DiffLnD    => _active.DiffLnD;
    public static TGAttribute DiffHdr    => _active.DiffHdr;
    public static TGAttribute DiffCtx    => _active.DiffCtx;
    public static TGAttribute DiffLnC    => _active.DiffLnC;
    public static TGAttribute SelFg      => _active.SelFg;
    public static TGAttribute SelHot     => _active.SelHot;
    public static TGAttribute Running    => _active.Running;
    public static TGAttribute Code       => _active.Code;
    public static TGAttribute ActiveSection => _active.ActiveSection;
    public static TGAttribute SynKeyword => _active.SynKeyword;
    public static TGAttribute SynType    => _active.SynType;
    public static TGAttribute SynString  => _active.SynString;
    public static TGAttribute SynNumber  => _active.SynNumber;
    public static TGAttribute StatusBg   => _active.StatusBg;

    public static ColorScheme Scheme() => new()
    {
        Normal    = _active.Normal,
        Focus     = _active.SelFg,
        HotNormal = _active.Cmd,
        HotFocus  = _active.SelHot,
        Disabled  = _active.Dim
    };

    public static ColorScheme FocusedPanelScheme() => new()
    {
        Normal    = _active.Bright,
        Focus     = _active.SelFg,
        HotNormal = _active.Cmd,
        HotFocus  = _active.SelHot,
        Disabled  = _active.Dim
    };

    // Read-only text panels (conversation, inspection). TextView draws body text AND the empty
    // background with ColorScheme.Focus, so Focus must carry the theme's main background — otherwise
    // the panel background takes the selection color (e.g. gray) instead of the editor background.
    // Body text uses its own baked cell attributes; Focus only colors empty space and selection.
    public static ColorScheme ReadOnlyTextScheme() => new()
    {
        Normal    = _active.Normal,
        Focus     = _active.Normal,
        HotNormal = _active.Cmd,
        HotFocus  = _active.SelHot,
        Disabled  = _active.Dim
    };

    // The command entry field. TextView draws its text with ColorScheme.Focus, so Input drives
    // both Normal and Focus to keep typed text the same color whether or not the field is focused.
    public static ColorScheme InputScheme() => new()
    {
        Normal    = _active.Input,
        Focus     = _active.Input,
        HotNormal = _active.Cmd,
        HotFocus  = _active.SelHot,
        Disabled  = _active.Dim
    };

    public static ColorScheme BtnScheme() => new()
    {
        Normal    = _active.Bright,
        Focus     = _active.BtnFocus,
        HotNormal = _active.Bright,
        HotFocus  = _active.BtnHotFocus,
        Disabled  = _active.Dim
    };

    public static ColorScheme StatusScheme() => new()
    {
        Normal    = _active.StatusBg,
        Focus     = _active.StatusBg,
        HotNormal = _active.StatusAccent,
        HotFocus  = _active.StatusAccent,
        Disabled  = _active.StatusDisabled
    };

    public static ColorScheme WarnStatusScheme() => Uniform(_active.WarnStatus);
    public static ColorScheme ErrStatusScheme() => Uniform(_active.ErrStatus);

    private static ColorScheme Uniform(TGAttribute attr) => new()
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
        if (name.Length == 0)
            name = "dark";

        var known = name is "dark" or "light" or "borland" or "system";
        var resolved = name switch
        {
            "light"   => "light",
            "borland" => "borland",
            "system"  => DetectSystem(),
            _         => "dark",   // "dark" and any unknown name
        };

        RequestedName = name;
        ActiveName = resolved;
        _active = Themes.ByName(resolved);
        return (resolved, !known);
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

        var newByRole = _active.AsRoleMap();
        return a => oldByAttr.TryGetValue(a, out var role) && newByRole.TryGetValue(role, out var na)
            ? na
            : a;
    }

    // Best-effort terminal light/dark detection. COLORFGBG ("fg;bg") is set by several terminals;
    // a light background index (7 or 15) means light. Falls back to dark when unsupported.
    private static string DetectSystem()
    {
        var fgbg = Environment.GetEnvironmentVariable("COLORFGBG");
        if (!string.IsNullOrWhiteSpace(fgbg))
        {
            var parts = fgbg.Split(';');
            if (parts.Length >= 2 && int.TryParse(parts[^1].Trim(), out var bg) && (bg == 7 || bg == 15))
                return "light";
        }
        return "dark";
    }
}
