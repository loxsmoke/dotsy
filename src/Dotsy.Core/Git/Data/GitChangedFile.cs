namespace Dotsy.Core.Git.Data;

public sealed record GitChangedFile(string Path, bool IsNew, bool IsDeleted);
