namespace Emanager.Core.Tasks;

public enum TaskPriority { Low = 2, Normal = 1, High = 0 }
public enum BackgroundTaskState { Queued, Running, Completed, Cancelled, Failed }
public sealed record BackgroundTaskInfo(Guid Id, string Name, TaskPriority Priority, BackgroundTaskState State, Exception? Error = null);

public sealed class BackgroundTaskManager : IAsyncDisposable
{
    private sealed record Work(Guid Id, string Name, TaskPriority Priority, Func<CancellationToken, Task<object?>> Execute, CancellationToken Token, TaskCompletionSource<object?> Completion);
    private readonly PriorityQueue<Work, int> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<BackgroundTaskInfo> _history = new();
    private readonly object _gate = new();
    private readonly Task[] _workers;

    public BackgroundTaskManager(int maxConcurrency = 2) => _workers = Enumerable.Range(0, Math.Max(1, maxConcurrency)).Select(_ => Task.Run(WorkerAsync)).ToArray();
    public IReadOnlyList<BackgroundTaskInfo> History { get { lock (_gate) return _history.ToArray(); } }

    public Task<T> EnqueueAsync<T>(string name, Func<CancellationToken, Task<T>> work, TaskPriority priority = TaskPriority.Normal, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name); ArgumentNullException.ThrowIfNull(work);
        var id = Guid.NewGuid(); var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new Work(id, name, priority, async token => await work(token), cancellationToken, completion);
        lock (_gate) { _queue.Enqueue(item, (int)priority); _history.Add(new(id, name, priority, BackgroundTaskState.Queued)); }
        _signal.Release(); return AwaitResultAsync<T>(completion.Task);
    }

    private static async Task<T> AwaitResultAsync<T>(Task<object?> task) => (T)(await task)!;
    private async Task WorkerAsync()
    {
        try
        {
            while (true)
            {
                await _signal.WaitAsync(_shutdown.Token); Work? item = null;
                lock (_gate) if (_queue.Count > 0) item = _queue.Dequeue();
                if (item is null) continue;
                Update(item.Id, BackgroundTaskState.Running);
                try { item.Token.ThrowIfCancellationRequested(); var result = await item.Execute(item.Token); Update(item.Id, BackgroundTaskState.Completed); item.Completion.TrySetResult(result); }
                catch (OperationCanceledException ex) { Update(item.Id, BackgroundTaskState.Cancelled, ex); item.Completion.TrySetCanceled(item.Token.IsCancellationRequested ? item.Token : new CancellationToken(true)); }
                catch (Exception ex) { Update(item.Id, BackgroundTaskState.Failed, ex); item.Completion.TrySetException(ex); }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }
    private void Update(Guid id, BackgroundTaskState state, Exception? error = null) { lock (_gate) { var index = _history.FindIndex(x => x.Id == id); if (index >= 0) _history[index] = _history[index] with { State = state, Error = error }; } }
    public async ValueTask DisposeAsync() { _shutdown.Cancel(); _signal.Release(_workers.Length); try { await Task.WhenAll(_workers); } catch (OperationCanceledException) { } _signal.Dispose(); _shutdown.Dispose(); }
}
