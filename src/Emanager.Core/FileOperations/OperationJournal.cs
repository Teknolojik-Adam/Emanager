using System.Text.Json;

namespace Emanager.Core.FileOperations;

public sealed record JournalEntry(Guid JobId, FileOperationKind Kind, string Source, string Destination,
    FileOperationState State, DateTimeOffset UpdatedAt, string? Error = null);

public sealed class OperationJournal
{
    private readonly string _path;
    private readonly object _gate = new();
    public OperationJournal(string? path = null) => _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "emanager", "operations.jsonl");

    public void Append(JournalEntry entry)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.AppendAllText(_path, JsonSerializer.Serialize(entry) + Environment.NewLine);
        }
    }

    public IReadOnlyList<JournalEntry> Recoverable()
    {
        if (!File.Exists(_path)) return Array.Empty<JournalEntry>();
        lock (_gate)
        {
            return File.ReadLines(_path).Select(line =>
            {
                try { return JsonSerializer.Deserialize<JournalEntry>(line); } catch (JsonException) { return null; }
            }).Where(x => x is not null).Cast<JournalEntry>()
              .GroupBy(x => x.JobId).Select(g => g.Last())
              .Where(x => x.State is FileOperationState.Queued or FileOperationState.Running or FileOperationState.Paused)
              .ToArray();
        }
    }
}
