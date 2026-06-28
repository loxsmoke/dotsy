using System.Text.Json;
using TGAttribute = Terminal.Gui.Drawing.Attribute;

internal static class JsonlFormatter
{
    private static readonly JsonSerializerOptions PrettyOptions = new() { WriteIndented = true };

    public static List<SourceLine> Load(string path)
    {
        var result = new List<SourceLine>();
        var recordNumber = 1;

        foreach (var rawLine in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                result.Add(Plain(""));
                recordNumber++;
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(rawLine);
                var pretty = JsonSerializer.Serialize(document.RootElement, PrettyOptions);
                result.Add(Plain($"// record {recordNumber}"));
                foreach (var line in pretty.Split('\n'))
                    result.Add(new SourceLine(JsonSyntax.Highlight(line.TrimEnd('\r'))));
            }
            catch (JsonException ex)
            {
                result.Add(Plain($"// record {recordNumber}: invalid JSONL ({ex.Message})", Palette.Error));
                result.Add(new SourceLine(JsonSyntax.Highlight(rawLine)));
            }

            recordNumber++;
        }

        if (result.Count == 0)
            result.Add(Plain("// empty file", Palette.Dim));

        return result;
    }

    private static SourceLine Plain(string text, TGAttribute? attribute = null) =>
        new([new Segment(text, attribute ?? Palette.Normal)]);
}
