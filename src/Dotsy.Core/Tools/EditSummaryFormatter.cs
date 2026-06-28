namespace Dotsy.Core.Tools;

public static class EditSummaryFormatter
{
    public static string LineDelta(int added, int deleted)
    {
        if (added == 0 && deleted == 0)
            return "";

        var parts = new List<string>();
        if (added > 0)
            parts.Add($"+{added}");
        if (deleted > 0)
            parts.Add($"-{deleted}");

        return "  " + string.Join(" ", parts) + " lines";
    }

    public static int CountLines(string text) =>
        string.IsNullOrEmpty(text) ? 0 : text.TrimEnd('\n').Split('\n').Length;
}
