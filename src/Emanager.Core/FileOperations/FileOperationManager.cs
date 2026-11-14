using System.Collections.Concurrent;
using System.Diagnostics;

namespace Emanager.Core.FileOperations;

public sealed class FileOperationManager
{
    private readonly ISafeFileOperations _safe;
    private readonly OperationJournal _journal;
    private readonly SemaphoreSlim _slots;
    private readonly IFileConflictResolver _conflicts;
    private readonly ConcurrentDictionary<Guid, (FileOperationJob Job, CancellationTokenSource Cts, PauseGate Pause)> _running = new();

    public FileOperationManager(ISafeFileOperations safe, OperationJournal journal, IFileConflictResolver? conflicts = null, int maxConcurrency = 2)
    {
        _safe = safe; _journal = journal; _conflicts = conflicts ?? new FileConflictResolver();
        _slots = new SemaphoreSlim(Math.Max(1, maxConcurrency));
    }

    public event EventHandler<(FileOperationJob Job, TransferProgress Progress)>? ProgressChanged;
    public IReadOnlyList<JournalEntry> Recoverable => _journal.Recoverable();
    public ConflictDecision ResolveConflict(FileConflict conflict, ConflictAction action) => _conflicts.Resolve(conflict, action);

    public Task<FileOperationResult> EnqueueAsync(FileOperationJob job, CancellationToken cancellationToken = default)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _running[job.Id] = (job, linked, new PauseGate());
        return RunAsync(job, linked);
    }

    public void Cancel(Guid jobId) { if (_running.TryGetValue(jobId, out var value)) value.Cts.Cancel(); }
    public void Pause(Guid jobId) { if (_running.TryGetValue(jobId, out var value)) { value.Pause.Reset(); _journal.Append(new(jobId, value.Job.Kind, value.Job.Source, value.Job.Destination, FileOperationState.Paused, DateTimeOffset.UtcNow)); } }
    public void Resume(Guid jobId) { if (_running.TryGetValue(jobId, out var value)) value.Pause.Set(); }
    public async Task<IReadOnlyList<FileOperationResult>> ResumeRecoverableAsync(CancellationToken cancellationToken = default)
    {
        var jobs = Recoverable.Select(x => new FileOperationJob(x.JobId, x.Kind, x.Source, x.Destination, x.UpdatedAt)).ToArray();
        return await Task.WhenAll(jobs.Select(job => EnqueueAsync(job, cancellationToken)));
    }

    private async Task<FileOperationResult> RunAsync(FileOperationJob job, CancellationTokenSource cts)
    {
        var token = cts.Token;
        var pause = _running[job.Id].Pause;
        _journal.Append(new(job.Id, job.Kind, job.Source, job.Destination, FileOperationState.Queued, DateTimeOffset.UtcNow));
        try
        {
            await _slots.WaitAsync(token);
            try
            {
                _journal.Append(new(job.Id, job.Kind, job.Source, job.Destination, FileOperationState.Running, DateTimeOffset.UtcNow));
                var watch = Stopwatch.StartNew();
                Action<long> progress = bytes => { pause.Wait(token); ProgressChanged?.Invoke(this, (job,
                    new TransferProgress(bytes, job.TotalBytes, watch.Elapsed.TotalSeconds <= 0 ? bytes : bytes / watch.Elapsed.TotalSeconds,
                        job.TotalBytes > bytes && bytes > 0 ? TimeSpan.FromSeconds((job.TotalBytes - bytes) / Math.Max(1, bytes / watch.Elapsed.TotalSeconds)) : null))); };
                var destinationPath = job.Kind == FileOperationKind.Rename ? job.Destination : Path.Combine(job.Destination, Path.GetFileName(job.Source));
                var conflict = File.Exists(destinationPath) || Directory.Exists(destinationPath);
                if (conflict && job.Conflict is null) throw new IOException("Destination already exists; a conflict decision is required.");
                if (conflict && job.Conflict == ConflictAction.Skip)
                {
                    _journal.Append(new(job.Id, job.Kind, job.Source, job.Destination, FileOperationState.Skipped, DateTimeOffset.UtcNow));
                    return new(job.Id, FileOperationState.Skipped);
                }
                if (conflict && job.Conflict == ConflictAction.NewerOnly)
                {
                    var sourceInfo = new FileInfo(job.Source); var destinationInfo = new FileInfo(destinationPath);
                    if (sourceInfo.LastWriteTimeUtc <= destinationInfo.LastWriteTimeUtc)
                    {
                        _journal.Append(new(job.Id, job.Kind, job.Source, job.Destination, FileOperationState.Skipped, DateTimeOffset.UtcNow));
                        return new(job.Id, FileOperationState.Skipped);
                    }
                }
                switch (job.Kind)
                {
                    case FileOperationKind.Copy when conflict && job.Conflict == ConflictAction.Overwrite: await _safe.ReplaceAsync(job.Source, job.Destination, false, token, progress); break;
                    case FileOperationKind.Move when conflict && job.Conflict == ConflictAction.Overwrite: await _safe.ReplaceAsync(job.Source, job.Destination, true, token, progress); break;
                    case FileOperationKind.Copy: await _safe.CopyAsync(job.Source, job.Destination, token, progress); break;
                    case FileOperationKind.Move: await _safe.MoveAsync(job.Source, job.Destination, token, progress); break;
                    case FileOperationKind.Rename: await _safe.RenameAsync(job.Source, Path.GetFileName(job.Destination), token); break;
                    default: throw new ArgumentOutOfRangeException();
                }
            }
            finally { _slots.Release(); }
            _journal.Append(new(job.Id, job.Kind, job.Source, job.Destination, FileOperationState.Completed, DateTimeOffset.UtcNow));
            return new(job.Id, FileOperationState.Completed);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _journal.Append(new(job.Id, job.Kind, job.Source, job.Destination, FileOperationState.Cancelled, DateTimeOffset.UtcNow));
            return new(job.Id, FileOperationState.Cancelled);
        }
        catch (Exception ex)
        {
            _journal.Append(new(job.Id, job.Kind, job.Source, job.Destination, FileOperationState.Failed, DateTimeOffset.UtcNow, ex.Message));
            return new(job.Id, FileOperationState.Failed, ex);
        }
        finally { _running.TryRemove(job.Id, out _); cts.Dispose(); }
    }
}

internal sealed class PauseGate
{
    private readonly ManualResetEventSlim _gate = new(true);
    public void Reset() => _gate.Reset();
    public void Set() => _gate.Set();
    public void Wait(CancellationToken token) => _gate.Wait(token);
}
