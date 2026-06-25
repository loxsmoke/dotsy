namespace Dotsy.Cli.Tui;

/// <summary>One-column view that draws T-junctions where the two panel borders meet.</summary>
internal sealed class PaneDivider : View
{
    protected override bool OnDrawingContent(DrawContext? context)
    {
        if (TuiSessionContext.App.Driver is null) return base.OnDrawingContent(context);
        TuiSessionContext.App.Driver.SetAttribute(GetScheme().Normal);
        int h = Frame.Height;
        for (int y = 0; y < h; y++)
        {
            Move(0, y);
            TuiSessionContext.App.Driver.AddRune(new System.Text.Rune(y == 0 ? '\u252c' : y == h - 1 ? '\u2534' : '\u2502'));
        }
        return true;
    }
}
