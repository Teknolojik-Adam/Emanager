namespace Emanager.Core.Synchronization;

public enum ComparisonKind { Same, OnlyInLeft, OnlyInRight, Changed, Conflict }
public enum SyncDirection { LeftToRight, RightToLeft, TwoWay }
public enum SyncMode { Merge, Mirror }
public enum SyncActionKind { Copy, Delete, Overwrite, Conflict }
public sealed record ComparisonEntry(string LeftPath, string RightPath, ComparisonKind Kind, DateTimeOffset? LeftModified, DateTimeOffset? RightModified);
public sealed record SyncAction(SyncActionKind Kind, string Source, string Destination, bool RequiresConfirmation);
public sealed record SyncPlan(IReadOnlyList<SyncAction> Actions);
public interface IFolderComparisonService { Task<IReadOnlyList<ComparisonEntry>> CompareAsync(string left, string right, CancellationToken cancellationToken = default); }
public interface IFolderSyncService { SyncPlan Plan(IReadOnlyList<ComparisonEntry> entries, SyncDirection direction, SyncMode mode = SyncMode.Merge); }
public interface ISyncExecutor { Task<IReadOnlyList<SyncAction>> ExecuteAsync(SyncPlan plan, bool confirmDestructive, Func<SyncAction, Task>? onAction = null, CancellationToken cancellationToken = default); }

public sealed class FolderComparisonService : IFolderComparisonService
{
    public async Task<IReadOnlyList<ComparisonEntry>> CompareAsync(string left, string right, CancellationToken cancellationToken = default)
    {
        var all = new HashSet<string>(Directory.EnumerateFiles(left, "*", SearchOption.AllDirectories).Select(x => Path.GetRelativePath(left, x)), StringComparer.Ordinal);
        all.UnionWith(Directory.EnumerateFiles(right, "*", SearchOption.AllDirectories).Select(x => Path.GetRelativePath(right, x)));
        var entries = new List<ComparisonEntry>();
        foreach (var relative in all)
        {
            cancellationToken.ThrowIfCancellationRequested(); var l = Path.Combine(left, relative); var r = Path.Combine(right, relative);
            var le = File.Exists(l) ? new FileInfo(l) : null; var re = File.Exists(r) ? new FileInfo(r) : null;
            var kind = le is null ? ComparisonKind.OnlyInRight : re is null ? ComparisonKind.OnlyInLeft : await HaveSameContentAsync(l, r, cancellationToken) ? ComparisonKind.Same : ComparisonKind.Changed;
            entries.Add(new(l, r, kind, le is null ? null : le.LastWriteTimeUtc, re is null ? null : re.LastWriteTimeUtc));
        }
        return entries;
    }

    private static async Task<bool> HaveSameContentAsync(string left, string right, CancellationToken cancellationToken)
    {
        var leftInfo = new FileInfo(left);
        var rightInfo = new FileInfo(right);
        if (leftInfo.Length != rightInfo.Length) return false;
        await using var leftStream = new FileStream(left, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var rightStream = new FileStream(right, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var leftHash = await System.Security.Cryptography.SHA256.HashDataAsync(leftStream, cancellationToken);
        var rightHash = await System.Security.Cryptography.SHA256.HashDataAsync(rightStream, cancellationToken);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(leftHash, rightHash);
    }
}

public sealed class FolderSyncPlanner : IFolderSyncService
{
    public SyncPlan Plan(IReadOnlyList<ComparisonEntry> entries, SyncDirection direction, SyncMode mode = SyncMode.Merge)
    {
        var actions = new List<SyncAction>();
        foreach (var entry in entries)
        {
            if (entry.Kind == ComparisonKind.OnlyInLeft && direction is SyncDirection.LeftToRight or SyncDirection.TwoWay) actions.Add(new(SyncActionKind.Copy, entry.LeftPath, entry.RightPath, false));
            else if (entry.Kind == ComparisonKind.OnlyInRight && direction is SyncDirection.RightToLeft or SyncDirection.TwoWay) actions.Add(new(SyncActionKind.Copy, entry.RightPath, entry.LeftPath, false));
            else if (entry.Kind == ComparisonKind.Changed)
            {
                if (direction == SyncDirection.TwoWay) actions.Add(new(SyncActionKind.Conflict, entry.LeftPath, entry.RightPath, true));
                else if (direction == SyncDirection.LeftToRight) actions.Add(new(SyncActionKind.Overwrite, entry.LeftPath, entry.RightPath, true));
                else if (direction == SyncDirection.RightToLeft) actions.Add(new(SyncActionKind.Overwrite, entry.RightPath, entry.LeftPath, true));
            }
            else if (entry.Kind == ComparisonKind.OnlyInRight && direction == SyncDirection.LeftToRight && mode == SyncMode.Mirror)
                actions.Add(new(SyncActionKind.Delete, entry.RightPath, entry.RightPath, true));
            else if (entry.Kind == ComparisonKind.OnlyInLeft && direction == SyncDirection.RightToLeft && mode == SyncMode.Mirror)
                actions.Add(new(SyncActionKind.Delete, entry.LeftPath, entry.LeftPath, true));
        }
        return new(actions);
    }
}

public sealed class FolderSyncExecutor : ISyncExecutor
{
    public async Task<IReadOnlyList<SyncAction>> ExecuteAsync(SyncPlan plan, bool confirmDestructive, Func<SyncAction, Task>? onAction = null, CancellationToken cancellationToken = default)
    {
        if (!confirmDestructive && plan.Actions.Any(x => x.RequiresConfirmation || x.Kind == SyncActionKind.Delete))
            throw new InvalidOperationException("Destructive synchronization requires explicit confirmation.");
        var completed = new List<SyncAction>();
        foreach (var action in plan.Actions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (action.Kind == SyncActionKind.Conflict) throw new InvalidOperationException($"Unresolved synchronization conflict: {action.Source}");
            if (action.Kind is SyncActionKind.Copy or SyncActionKind.Overwrite)
            {
                var destinationDirectory = Path.GetDirectoryName(action.Destination)!;
                Directory.CreateDirectory(destinationDirectory);
                var stagingPath = Path.Combine(destinationDirectory, $".{Path.GetFileName(action.Destination)}.{Guid.NewGuid():N}.tmp");
                try
                {
                    await using (var input = new FileStream(action.Source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    await using (var output = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    {
                        await input.CopyToAsync(output, cancellationToken);
                        await output.FlushAsync(cancellationToken);
                        output.Flush(true);
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Move(stagingPath, action.Destination, true);
                }
                finally
                {
                    if (File.Exists(stagingPath)) File.Delete(stagingPath);
                }
            }
            else if (action.Kind == SyncActionKind.Delete) File.Delete(action.Destination);
            completed.Add(action); if (onAction is not null) await onAction(action);
        }
        return completed;
    }
}
