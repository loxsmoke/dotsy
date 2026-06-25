namespace Dotsy.Cli.Tui.FileList;

internal sealed record FileRow(string Path, int Added, int Deleted, FileChangeType ChangeType, List<List<Cell>> Diff)
{
    public override string ToString() => ChangeType switch
    {
        FileChangeType.Added   => $"  + {Path}",
        FileChangeType.Deleted => $"  - {Path}",
        _                      => $"  ↳ {Path}   +{Added}  -{Deleted}"
    };
}
