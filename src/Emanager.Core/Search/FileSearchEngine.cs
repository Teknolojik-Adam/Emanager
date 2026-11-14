namespace Emanager.Core.Search;

public sealed record SearchQuery
{
    public string? NameContains { get; init; }
    public string? Extension { get; init; }
    public string? MimeType { get; init; }
    public long? MinimumSize { get; init; }
    public long? MaximumSize { get; init; }
    public DateTimeOffset? ModifiedAfter { get; init; }
    public DateTimeOffset? ModifiedBefore { get; init; }
    public bool SearchContents { get; init; }
    public string? ContentContains { get; init; }
    public bool IncludeHidden { get; init; }
    public bool IncludeSubdirectories { get; init; }
}
public sealed record SearchResult(string FullPath, string Name, long Length, DateTimeOffset Modified, string? MimeType);
public sealed record SearchProgress(long ItemsScanned, long ResultsFound);

public sealed class FileSearchEngine
{
    public async Task SearchAsync(string root, SearchQuery query, Action<SearchResult> onResult, CancellationToken cancellationToken = default, IProgress<SearchProgress>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root); ArgumentNullException.ThrowIfNull(query); ArgumentNullException.ThrowIfNull(onResult);
        var options = query.IncludeSubdirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        IEnumerable<string> paths;
        try { paths = Directory.EnumerateFileSystemEntries(root, "*", options); }
        catch (DirectoryNotFoundException) { paths = Array.Empty<string>(); }
        long scanned = 0, found = 0;
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            scanned++;
            FileSystemInfo info;
            try { info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path); } catch (IOException) { continue; }
            if (info is not FileInfo file || (!query.IncludeHidden && IsHiddenPath(file.FullName, root))) continue;
            if (query.NameContains is { Length: > 0 } term && !file.Name.Contains(term, StringComparison.OrdinalIgnoreCase)) continue;
            if (query.Extension is { Length: > 0 } ext && !file.Extension.Equals(ext.StartsWith('.') ? ext : "." + ext, StringComparison.OrdinalIgnoreCase)) continue;
            var mimeType = GuessMime(file.Extension);
            if (query.MimeType is { Length: > 0 } mime && !mimeType.Equals(mime, StringComparison.OrdinalIgnoreCase)) continue;
            if (query.MinimumSize is { } min && file.Length < min || query.MaximumSize is { } max && file.Length > max) continue;
            var modified = new DateTimeOffset(file.LastWriteTimeUtc);
            if (query.ModifiedAfter is { } after && modified < after || query.ModifiedBefore is { } before && modified > before) continue;
            if (query.SearchContents && query.ContentContains is { } content && !await ContainsAsync(file.FullName, content, cancellationToken)) continue;
            onResult(new(file.FullName, file.Name, file.Length, modified, mimeType));
            found++; progress?.Report(new(scanned, found));
        }
    }

    private static async Task<bool> ContainsAsync(string path, string term, CancellationToken token)
    {
        if (new FileInfo(path).Length > 64 * 1024 * 1024) return false;
        try { using var reader = new StreamReader(path); while (await reader.ReadLineAsync(token) is { } line) if (line.Contains(term, StringComparison.OrdinalIgnoreCase)) return true; }
        catch (IOException) { }
        return false;
    }
    private static string GuessMime(string extension) => extension.ToLowerInvariant() switch { ".txt" or ".cs" or ".json" or ".xml" => "text/plain", ".pdf" => "application/pdf", _ => "application/octet-stream" };
    private static bool IsHiddenPath(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries).Any(segment => segment.StartsWith(".", StringComparison.Ordinal));
    }
}
