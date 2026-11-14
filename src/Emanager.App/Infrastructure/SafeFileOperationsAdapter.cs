using Emanager.Core.FileOperations;

namespace Emanager.App.Infrastructure;

/// <summary>Core adapter that preserves the existing SafeFileOperations security boundary.</summary>
public sealed class SafeFileOperationsAdapter : ISafeFileOperations
{
    public Task CopyAsync(string source, string destinationDirectory, CancellationToken cancellationToken, Action<long>? progress) =>
        Emanager.App.SafeFileOperations.CopyAsync(source, destinationDirectory, cancellationToken, progress);

    public Task MoveAsync(string source, string destinationDirectory, CancellationToken cancellationToken, Action<long>? progress) =>
        Emanager.App.SafeFileOperations.MoveAsync(source, destinationDirectory, cancellationToken, progress);

    public Task RenameAsync(string source, string newName, CancellationToken cancellationToken) =>
        Emanager.App.SafeFileOperations.RenameAsync(source, newName, cancellationToken);

    public Task ReplaceAsync(string source, string destinationDirectory, bool move, CancellationToken cancellationToken, Action<long>? progress) =>
        Emanager.App.SafeFileOperations.ReplaceAsync(source, destinationDirectory, move, cancellationToken, progress);
}
