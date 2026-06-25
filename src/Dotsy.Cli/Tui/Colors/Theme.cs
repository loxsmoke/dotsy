namespace Dotsy.Cli.Tui.Colors;

// A complete set of color attributes for the TUI. The active theme is resolved at startup
// (and on live re-theme) and every color the UI draws is read from it via Palette.
internal sealed class Theme
{
    public required string Name { get; init; }
    public required TGAttribute Normal { get; init; }
    public required TGAttribute Dim { get; init; }
    public required TGAttribute Bright { get; init; }
    public required TGAttribute Cmd { get; init; }
    public required TGAttribute Success { get; init; }
    public required TGAttribute Err { get; init; }
    public required TGAttribute Warn { get; init; }
    public required TGAttribute Bullet { get; init; }
    public required TGAttribute Sub { get; init; }
    public required TGAttribute Running { get; init; }
    public required TGAttribute Code { get; init; }

    // Highlight for the active model's section header in /config (e.g. [model.openai]).
    public required TGAttribute ActiveSection { get; init; }

    public required TGAttribute DiffAdd { get; init; }
    public required TGAttribute DiffDel { get; init; }
    public required TGAttribute DiffLnA { get; init; }
    public required TGAttribute DiffLnD { get; init; }
    public required TGAttribute DiffHdr { get; init; }
    public required TGAttribute DiffCtx { get; init; }
    public required TGAttribute DiffLnC { get; init; }

    public required TGAttribute SelFg { get; init; }
    public required TGAttribute SelHot { get; init; }

    // Highlight bar for the selected row in the file/tool list panels.
    public required TGAttribute SelRow { get; init; }

    // Typed text in the command entry field (TextView draws text with Scheme.Focus).
    public required TGAttribute Input { get; init; }

    public required TGAttribute SynKeyword { get; init; }
    public required TGAttribute SynType { get; init; }
    public required TGAttribute SynString { get; init; }
    public required TGAttribute SynNumber { get; init; }

    // Status bar + button chrome (used by the Scheme builders in Palette).
    public required TGAttribute StatusBg { get; init; }
    public required TGAttribute StatusAccent { get; init; }
    public required TGAttribute StatusDisabled { get; init; }
    public required TGAttribute BtnFocus { get; init; }
    public required TGAttribute BtnHotFocus { get; init; }
    public required TGAttribute WarnStatus { get; init; }
    public required TGAttribute ErrStatus { get; init; }

    // Maps each semantic role name to its concrete attribute in this theme. Used to recolor
    // already-rendered cells on a live theme switch: an old cell's attribute is reverse-looked-up
    // to its role, then re-resolved through the new theme. (Reflection over ~30 properties runs
    // only on the rare theme-change event.)
    public Dictionary<string, TGAttribute> AsRoleMap()
    {
        var map = new Dictionary<string, TGAttribute>();
        foreach (var p in typeof(Theme).GetProperties())
            if (p.PropertyType == typeof(TGAttribute) && p.GetValue(this) is TGAttribute a)
                map[p.Name] = a;
        return map;
    }
}
