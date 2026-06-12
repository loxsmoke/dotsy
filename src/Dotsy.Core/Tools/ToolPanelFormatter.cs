using System.Text.Json;
using Dotsy.Core.Utils;

namespace Dotsy.Core.Tools;

public static class ToolPanelFormatter
{
    // Returns the raw content string for Write tool so the inspect panel can display it.
    public static string? GetWriteContent(string argsJson)
    {
        try
        {
            var args = JsonDocument.Parse(argsJson).RootElement;
            return args.TryGetProperty("content", out _) ? args.GetStringPropertyOrEmpty("content") : null;
        }
        catch { return null; }
    }
}
