namespace Emanager.Core.Watchers;

public enum FileChangeKind { Created, Changed, Deleted, Renamed, RescanRequired }
public sealed record FileChange(FileChangeKind Kind, string Path, string? OldPath = null);
public sealed class FileSystemWatchService : IDisposable
{
    private readonly FileSystemWatcher _watcher; private readonly Timer _timer; private readonly object _gate = new(); private readonly HashSet<FileChange> _pending = new();
    public FileSystemWatchService(string path, TimeSpan? debounce = null)
    {
        _watcher = new(path) { IncludeSubdirectories = false, EnableRaisingEvents = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
        _watcher.Created += (_, e) => Add(new(FileChangeKind.Created, e.FullPath)); _watcher.Changed += (_, e) => Add(new(FileChangeKind.Changed, e.FullPath)); _watcher.Deleted += (_, e) => Add(new(FileChangeKind.Deleted, e.FullPath)); _watcher.Renamed += (_, e) => Add(new(FileChangeKind.Renamed, e.FullPath, e.OldFullPath)); _watcher.Error += (_, _) => Add(new(FileChangeKind.RescanRequired, path));
        _timer = new(_ => Flush(), null, debounce ?? TimeSpan.FromMilliseconds(250), debounce ?? TimeSpan.FromMilliseconds(250));
    }
    public event EventHandler<IReadOnlyList<FileChange>>? Changes;
    private void Add(FileChange change) { lock (_gate) _pending.Add(change); }
    private void Flush() { FileChange[] changes; lock (_gate) { if (_pending.Count == 0) return; changes = _pending.ToArray(); _pending.Clear(); } Changes?.Invoke(this, changes); }
    public void Dispose() { _watcher.Dispose(); _timer.Dispose(); }
}
