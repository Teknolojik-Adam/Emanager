namespace Emanager.Core.Synchronization;

public enum ComparisonKind { Same, OnlyInLeft, OnlyInRight, Changed, Conflict }
public enum SyncDirection { LeftToRight, RightToLeft, TwoWay }
public enum SyncActionKind { Copy, Delete, Overwrite, Conflict }
public sealed record ComparisonEntry(string LeftPath, string RightPath, ComparisonKind Kind, DateTimeOffset? LeftModified, DateTimeOffset? RightModified);
public sealed record SyncAction(SyncActionKind Kind, string Source, string Destination, bool RequiresConfirmation);
public sealed record SyncPlan(IReadOnlyList<SyncAction> Actions);
public interface IFolderComparisonService { Task<IReadOnlyList<ComparisonEntry>> CompareAsync(string left, string right, CancellationToken cancellationToken = default); }
public interface IFolderSyncService { SyncPlan Plan(IReadOnlyList<ComparisonEntry> entries, SyncDirection direction); }
public interface ISyncExecutor { Task<IReadOnlyList<SyncAction>> ExecuteAsync(SyncPlan plan, bool confirmDestructive, Func<SyncAction, Task>? onAction = null, CancellationToken cancellationToken = default); }

public sealed class FolderComparisonService : IFolderComparisonService
{
    public Task<IReadOnlyList<ComparisonEntry>> CompareAsync(string left, string right, CancellationToken cancellationToken = default)
    {
        var all = new HashSet<string>(Directory.EnumerateFiles(left, "*", SearchOption.AllDirectories).Select(x => Path.GetRelativePath(left, x)), StringComparer.Ordinal);
        all.UnionWith(Directory.EnumerateFiles(right, "*", SearchOption.AllDirectories).Select(x => Path.GetRelativePath(right, x)));
        var entries = new List<ComparisonEntry>();
        foreach (var relative in all)
        {
            cancellationToken.ThrowIfCancellationRequested(); var l = Path.Combine(left, relative); var r = Path.Combine(right, relative);
            var le = File.Exists(l) ? new FileInfo(l) : null; var re = File.Exists(r) ? new FileInfo(r) : null;
            var kind = le is null ? ComparisonKind.OnlyInRight : re is null ? ComparisonKind.OnlyInLeft : le.Length == re.Length && le.LastWriteTimeUtc == re.LastWriteTimeUtc ? ComparisonKind.Same : ComparisonKind.Changed;
            entries.Add(new(l, r, kind, le is null ? null : le.LastWriteTimeUtc, re is null ? null : re.LastWriteTimeUtc));
        }
        return Task.FromResult<IReadOnlyList<ComparisonEntry>>(entries);
    }
}

public sealed class FolderSyncPlanner : IFolderSyncService
{
    public SyncPlan Plan(IReadOnlyList<ComparisonEntry> entries, SyncDirection direction)
    {
        var actions = new List<SyncAction>();
        foreach (var entry in entries)
        {
            if (entry.Kind == ComparisonKind.OnlyInLeft && direction is SyncDirection.LeftToRight or SyncDirection.TwoWay) actions.Add(new(SyncActionKind.Copy, entry.LeftPath, entry.RightPath, false));
            else if (entry.Kind == ComparisonKind.OnlyInRight && direction is SyncDirection.RightToLeft or SyncDirection.TwoWay) actions.Add(new(SyncActionKind.Copy, entry.RightPath, entry.LeftPath, false));
            else if (entry.Kind == ComparisonKind.Changed)
            {
                if (direction == SyncDirection.TwoWay && entry.LeftModified != entry.RightModified) actions.Add(new(SyncActionKind.Conflict, entry.LeftPath, entry.RightPath, true));
                else if (direction == SyncDirection.LeftToRight) actions.Add(new(SyncActionKind.Overwrite, entry.LeftPath, entry.RightPath, true));
                else if (direction == SyncDirection.RightToLeft) actions.Add(new(SyncActionKind.Overwrite, entry.RightPath, entry.LeftPath, true));
            }
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
                Directory.CreateDirectory(Path.GetDirectoryName(action.Destination)!);
                await using var input = new FileStream(action.Source, FileMode.Open, FileAccess.Read, FileShare.Read);
                await using var output = new FileStream(action.Destination, FileMode.Create, FileAccess.Write, FileShare.None);
                await input.CopyToAsync(output, cancellationToken);
            }
            else if (action.Kind == SyncActionKind.Delete) File.Delete(action.Destination);
            completed.Add(action); if (onAction is not null) await onAction(action);
        }
        return completed;
    }
}
