using Dotsy.Cli.Tui.Colors;

namespace Dotsy.Cli.Tui;

// Bracket-free button: plain text, gray background on focus
internal sealed class FlatButton : View
{
    public event EventHandler? Fired;

    public FlatButton(string label, Pos x, Pos y) : this(label)
    {
        X = x;
        Y = y;
    }

    public FlatButton(string label)
    {
        var t = $" {label} ";
        Text = t;
        Width = t.Length;
        Height = 1;
        CanFocus = true;
        SetScheme(Palette.BtnScheme());
    }

    protected override bool OnKeyDown(Key key)
    {
        if (key.KeyCode == KeyCode.Enter || key.KeyCode == KeyCode.Space)
        {
            Fired?.Invoke(this, EventArgs.Empty);
            return true;
        }
        return base.OnKeyDown(key);
    }
}
