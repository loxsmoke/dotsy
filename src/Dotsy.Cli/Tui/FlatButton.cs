using Terminal.Gui;

namespace Dotsy.Cli.Tui;

// Bracket-free button: plain text, gray background on focus
internal sealed class FlatButton : View
{
    public event EventHandler? Fired;

    public FlatButton(string label)
    {
        var t = $"  {label}  ";
        Text = t;
        Width = t.Length;
        Height = 1;
        CanFocus = true;
        ColorScheme = Palette.BtnScheme();
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
