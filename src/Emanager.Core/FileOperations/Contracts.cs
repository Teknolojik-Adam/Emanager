namespace Emanager.Core.FileOperations;

public enum FileOperationKind { Copy, Move, Rename }
public enum FileOperationState { Queued, Running, Paused, Completed, Skipped, Cancelled, Failed }
public enum ConflictAction { Overwrite, Skip, Rename, Compare, NewerOnly }

public sealed record FileConflict(string Source, string Destination, long SourceLength, long DestinationLength,
    DateTimeOffset SourceModified, DateTimeOffset DestinationModified);
public sealed record ConflictDecision(ConflictAction Action, bool MayOverwrite, string? NewName = null);

public sealed record TransferProgress(long BytesTransferred, long TotalBytes, double BytesPerSecond, TimeSpan? Remaining,
    FileOperationState State = FileOperationState.Running)
{
    public double Fraction => TotalBytes <= 0 ? 0 : Math.Clamp((double)BytesTransferred / TotalBytes, 0, 1);
}

public interface IFileConflictResolver
{
    ConflictDecision Resolve(FileConflict conflict, ConflictAction action);
}

public interface ISafeFileOperations
{
    Task CopyAsync(string source, string destinationDirectory, CancellationToken cancellationToken, Action<long>? progress);
    Task MoveAsync(string source, string destinationDirectory, CancellationToken cancellationToken, Action<long>? progress);
    Task RenameAsync(string source, string newName, CancellationToken cancellationToken);
    Task ReplaceAsync(string source, string destinationDirectory, bool move, CancellationToken cancellationToken, Action<long>? progress);
}

public sealed record FileOperationJob(Guid Id, FileOperationKind Kind, string Source, string Destination,
    DateTimeOffset CreatedAt, long TotalBytes = 0, ConflictAction? Conflict = null);

public sealed record FileOperationResult(Guid JobId, FileOperationState State, Exception? Error = null);
