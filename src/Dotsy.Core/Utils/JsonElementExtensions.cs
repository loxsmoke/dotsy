using System.Text.Json;

namespace Dotsy.Core.Utils;

public static class JsonElementExtensions
{
    public static string GetStringPropertyOrEmpty(this JsonElement input, string propertyName) =>
        input.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
