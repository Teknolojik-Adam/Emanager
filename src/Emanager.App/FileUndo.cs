namespace Emanager.App;

/// <summary>Session-local, conservative undo of completed file operations.</summary>
public sealed class FileUndo
{
    public enum Kind { Copy, Move, Rename }
    public sealed record Step(Kind Operation, string Before, string After);
    private readonly Stack<List<Step>> history = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    public bool CanUndo => history.Count > 0;

    public void Record(IEnumerable<Step> steps)
    {
        var batch = steps.Where(x => x.Before != x.After).ToList();
        if (batch.Count == 0) return;
        history.Push(batch);
        if (history.Count > 30)
        {
            var latest = history.Reverse().Skip(1).ToArray();
            history.Clear();
            foreach (var item in latest) history.Push(item);
        }
    }

    public async Task<(int Restored, string? Error)> UndoAsync()
    {
        if (!await gate.WaitAsync(0)) return (0, "Another undo operation is running.");
        try
        {
            if (!CanUndo) return (0, "No operation to undo.");
            var batch = history.Peek();
            int done = 0;
            for (int i = batch.Count - 1; i >= 0; i--)
            {
                var step = batch[i];
                try
                {
                    if (!File.Exists(step.After) && !Directory.Exists(step.After))
                        throw new IOException("File no longer exists: " + step.After);
                    if (step.Operation == Kind.Copy)
                    {
                        // Undoing a copy never permanently deletes user data.
                        // Store the copied entry in a dedicated recovery area; never invoke
                        // Trash here because its cross-device fallback may permanently delete.
                        var recovery = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "emanager", "undo-recovery", Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(recovery);
                        await SafeFileOperations.MoveAsync(step.After, recovery);
                    }
                    else
                    {
                        if (File.Exists(step.Before) || Directory.Exists(step.Before))
                            throw new IOException("Original location is occupied: " + step.Before);
                        if (step.Operation == Kind.Rename)
                            await SafeFileOperations.RenameAsync(step.After, Path.GetFileName(step.Before));
                        else
                            await SafeFileOperations.MoveAsync(step.After, Path.GetDirectoryName(step.Before)!);
                    }
                    batch.RemoveAt(i);
                    done++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
                { return (done, ex.Message); }
            }
            history.Pop();
            return (done, null);
        }
        finally { gate.Release(); }
    }
}
