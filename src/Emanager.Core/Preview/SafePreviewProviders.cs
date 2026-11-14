using System.IO.Compression;

namespace Emanager.Core.Preview;

public sealed record FilePreviewMetadata(string Name, long Size, DateTimeOffset Modified, string Extension, string? MimeType);

public sealed class MetadataPreviewProvider : IPreviewProvider
{
    public bool CanHandle(string path, string? mimeType) => File.Exists(path);
    public Task<PreviewResult?> PreviewAsync(string path, CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(path);
        return Task.FromResult<PreviewResult?>(new PreviewResult(path, "metadata", new FilePreviewMetadata(info.Name, info.Length, info.LastWriteTimeUtc, info.Extension, null)));
    }
}

public sealed class ImagePreviewProvider : IPreviewProvider
{
    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif"];
    public bool CanHandle(string path, string? mimeType) => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) && File.Exists(path);
    public Task<PreviewResult?> PreviewAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult<PreviewResult?>(new(path, "image", new FilePreviewMetadata(Path.GetFileName(path), new FileInfo(path).Length, File.GetLastWriteTimeUtc(path), Path.GetExtension(path), null)));
}

public sealed class ArchivePreviewProvider : IPreviewProvider
{
    public bool CanHandle(string path, string? mimeType) => Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase) && File.Exists(path);
    public Task<PreviewResult?> PreviewAsync(string path, CancellationToken cancellationToken = default)
    {
        var entries = new List<string>();
        using var archive = ZipFile.OpenRead(path);
        foreach (var entry in archive.Entries.Take(2000)) { cancellationToken.ThrowIfCancellationRequested(); entries.Add(entry.FullName); }
        return Task.FromResult<PreviewResult?>(new(path, "archive", entries, archive.Entries.Count > entries.Count));
    }
}
