namespace Emanager.Core.FileOperations;

public sealed class FileConflictResolver : IFileConflictResolver
{
    public ConflictDecision Resolve(FileConflict conflict, ConflictAction action)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        return action switch
        {
            ConflictAction.Overwrite => new(action, true),
            ConflictAction.Skip => new(action, false),
            ConflictAction.Compare => new(action, false),
            ConflictAction.NewerOnly => new(action, conflict.SourceModified > conflict.DestinationModified),
            ConflictAction.Rename => new(action, false, BuildAvailableName(conflict.Destination)),
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
    }

    private static string BuildAvailableName(string destination)
    {
        var directory = Path.GetDirectoryName(destination) ?? ".";
        var name = Path.GetFileNameWithoutExtension(destination);
        var extension = Path.GetExtension(destination);
        return Path.Combine(directory, $"{name} (copy){extension}");
    }
}
