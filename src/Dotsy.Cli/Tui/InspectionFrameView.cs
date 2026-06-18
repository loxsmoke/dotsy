using Terminal.Gui;

namespace Dotsy.Cli.Tui;

internal sealed class InspectionFrameView : FrameView
{
    public TextView? ContentView { get; set; }

    protected override void OnDrawComplete(DrawContext? context)
    {
        base.OnDrawComplete(context);
        DrawScrollBars();
    }

    private void DrawScrollBars()
    {
        if (Application.Driver is null || ContentView is null)
            return;

        var frame = FrameToScreen();
        if (frame.Width <= 0 || frame.Height <= 0)
            return;

        int viewportWidth = Math.Max(1, ContentView.Viewport.Width);
        int viewportHeight = Math.Max(1, ContentView.Viewport.Height);
        var text = ContentView.Text?.ToString() ?? "";
        int contentLines = GetContentLineCount(text);
        int maxLineWidth = GetMaxLineWidth(text);
        bool showVertical = contentLines > viewportHeight;
        bool showHorizontal = maxLineWidth > viewportWidth;

        Application.Driver.SetAttribute(GetNormalColor());

        if (showVertical)
        {
            int height = frame.Height - 2;
            DrawVertical(frame.X + frame.Width - 1, frame.Y + 1, height, contentLines, viewportHeight, ContentView.TopRow);
        }

        if (showHorizontal)
        {
            int width = frame.Width - (showVertical ? 1 : 0);
            DrawHorizontal(frame.X, frame.Y + frame.Height - 1, width, maxLineWidth, viewportWidth, ContentView.LeftColumn);
        }
    }

    private static int GetMaxLineWidth(string text) =>
        text.Split('\n').Select(line => line.TrimEnd('\r').Length).DefaultIfEmpty(0).Max();

    private static int GetContentLineCount(string text)
    {
        text = text.Replace("\r\n", "\n").TrimEnd('\n');
        return text.Length == 0 ? 0 : text.Count(ch => ch == '\n') + 1;
    }

    private static void DrawVertical(int x, int y, int height, int totalRows, int viewportRows, int topRow)
    {
        if (height <= 0 || Application.Driver is null) return;
        if (height == 1)
        {
            Application.Driver.Move(x, y);
            Application.Driver.AddRune(new System.Text.Rune('░'));
            return;
        }

        Application.Driver.Move(x, y);
        Application.Driver.AddRune(new System.Text.Rune('▲'));
        if (height == 2)
        {
            Application.Driver.Move(x, y + 1);
            Application.Driver.AddRune(new System.Text.Rune('▼'));
            return;
        }

        int trackHeight = height - 2;
        int thumbHeight = Math.Max(1, trackHeight * viewportRows / Math.Max(1, totalRows));
        int maxTop = Math.Max(1, totalRows - viewportRows);
        int thumbTop = 1 + Math.Min(trackHeight - thumbHeight, topRow * (trackHeight - thumbHeight) / maxTop);

        for (int row = 1; row < height - 1; row++)
        {
            Application.Driver.Move(x, y + row);
            var ch = row >= thumbTop && row < thumbTop + thumbHeight ? '█' : '░';
            Application.Driver.AddRune(new System.Text.Rune(ch));
        }

        Application.Driver.Move(x, y + height - 1);
        Application.Driver.AddRune(new System.Text.Rune('▼'));
    }

    private static void DrawHorizontal(int x, int y, int width, int totalColumns, int viewportColumns, int leftColumn)
    {
        if (width <= 0 || Application.Driver is null) return;
        if (width == 1)
        {
            Application.Driver.Move(x, y);
            Application.Driver.AddRune(new System.Text.Rune('░'));
            return;
        }

        Application.Driver.Move(x, y);
        Application.Driver.AddRune(new System.Text.Rune('◄'));
        if (width == 2)
        {
            Application.Driver.Move(x + 1, y);
            Application.Driver.AddRune(new System.Text.Rune('►'));
            return;
        }

        int trackWidth = width - 2;
        int thumbWidth = Math.Max(1, trackWidth * viewportColumns / Math.Max(1, totalColumns));
        int maxLeft = Math.Max(1, totalColumns - viewportColumns);
        int thumbLeft = 1 + Math.Min(trackWidth - thumbWidth, leftColumn * (trackWidth - thumbWidth) / maxLeft);

        for (int col = 1; col < width - 1; col++)
        {
            Application.Driver.Move(x + col, y);
            var ch = col >= thumbLeft && col < thumbLeft + thumbWidth ? '█' : '░';
            Application.Driver.AddRune(new System.Text.Rune(ch));
        }

        Application.Driver.Move(x + width - 1, y);
        Application.Driver.AddRune(new System.Text.Rune('►'));
    }
}
