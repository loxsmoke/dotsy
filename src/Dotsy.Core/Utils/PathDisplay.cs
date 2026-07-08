namespace Dotsy.Core.Utils;

public static class PathDisplay
{
    public static string MakeRelative(string path, string cwd)
    {
        if (string.IsNullOrEmpty(path))
            return ".";

        try
        {
            var absolutePath = Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(cwd, path));
            var absoluteCwd = Path.GetFullPath(cwd);

            if (absolutePath.StartsWithNoCase(absoluteCwd + Path.DirectorySeparatorChar))
            {
                return absolutePath[(absoluteCwd.Length + 1)..];
            }

            if (absolutePath.EqualsNoCase(absoluteCwd))
                return ".";
        }
        catch
        {
            // Display formatting should not replace the original path with an error.
        }

        return path;
    }
}
