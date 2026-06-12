using System.Text;
using Terminal.Gui;

namespace Dotsy.Cli.Tui;

internal static class TextViewExtensions
{
    /// <summary>
    /// Loads Cell-based content into a TextView.
    /// </summary>
    public static void Load(this TextView textView, List<List<Cell>> lines)
    {
        if (textView == null || lines == null)
            return;

        // Convert Cell-based lines to plain text for TextView
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            foreach (var cell in line)
            {
                if (cell.Rune.Value is '\r' or '\n')
                    continue;
                sb.Append(cell.Rune);
            }
            sb.AppendLine();
        }

        textView.Text = sb.ToString();
    }

    /// <summary>
    /// Moves the cursor/scroll position to the end of the text content.
    /// Terminal.Gui TextView typically auto-scrolls when content is updated.
    /// </summary>
    public static void MoveEnd(this TextView textView)
    {
        // No-op: Terminal.Gui TextView handles scrolling automatically
        // This method exists for API compatibility with the existing code
    }
}
