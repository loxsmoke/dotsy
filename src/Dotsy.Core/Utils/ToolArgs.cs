using System;
using System.Collections.Generic;
using System.Text;

namespace Dotsy.Core.Utils;

public class ToolArgs
{
    public static System.Text.Json.JsonElement TryParseArgs(string args)
    {
        if (string.IsNullOrWhiteSpace(args))
            return System.Text.Json.JsonDocument.Parse("{}").RootElement;
        try
        {
            var el = System.Text.Json.JsonDocument.Parse(args).RootElement;
            // Some models double-encode tool arguments as a JSON string (e.g. "{\"path\":...}").
            // Unwrap one level when the string itself parses as an object/array.
            if (el.ValueKind == System.Text.Json.JsonValueKind.String
                && el.GetString() is { Length: > 0 } inner)
            {
                try
                {
                    var unwrapped = System.Text.Json.JsonDocument.Parse(inner).RootElement;
                    if (unwrapped.ValueKind is System.Text.Json.JsonValueKind.Object
                        or System.Text.Json.JsonValueKind.Array)
                        return unwrapped;
                }
                catch { /* not double-encoded JSON; fall through */ }
            }
            return el;
        }
        catch { return System.Text.Json.JsonDocument.Parse("{}").RootElement; }
    }
}
