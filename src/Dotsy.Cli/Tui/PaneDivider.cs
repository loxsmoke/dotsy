using Terminal.Gui;

namespace Dotsy.Cli.Tui;

/// <summary>One-column view that draws ┬ / │ / ┴ to form T-junctions where the two panel borders meet.</summary>
internal sealed class PaneDivider : View
{
    protected override bool OnDrawingContent()
    {
        if (Application.Driver is null) return base.OnDrawingContent();
        Application.Driver.SetAttribute(GetNormalColor());
        int h = Frame.Height;
        for (int y = 0; y < h; y++)
        {
            Move(0, y);
            Application.Driver.AddRune(new System.Text.Rune(y == 0 ? '┬' : y == h - 1 ? '┴' : '│'));
        }
        return true;
    }
}
