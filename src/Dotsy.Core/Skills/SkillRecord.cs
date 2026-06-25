namespace Dotsy.Core.Skills;

public sealed class SkillRecord
{
    /// <summary>
    /// The name of the skill derived from file name or the folder name
    /// </summary>
    public required string Name { get; init; }
    /// <summary>
    /// The contents of the file.
    /// </summary>
    public required string Body { get; init; }
    /// <summary>
    /// The full path to skill file. "Name" is based on this path and "Body" is the content of this file.
    /// </summary>
    public required string FilePath { get; init; }
    /// <summary>
    /// Other files in the same folder and sub-folders. Does not include the "FilePath"
    /// </summary>
    public required IReadOnlyList<string> CompanionPaths { get; init; }
}
