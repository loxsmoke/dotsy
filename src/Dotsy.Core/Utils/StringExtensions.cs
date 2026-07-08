namespace Dotsy.Core.Utils;

public static class StringExtensions
{
    public static bool EqualsNoCase(this string? str, string? other) =>
        string.Equals(str, other, StringComparison.OrdinalIgnoreCase);
    public static bool StartsWithNoCase(this string str, string prefix) =>
        str.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    public static bool EndsWithNoCase(this string str, string suffix) =>
        str.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
    public static bool ContainsNoCase(this string str, string substring) =>
        str.Contains(substring, StringComparison.OrdinalIgnoreCase);
}
