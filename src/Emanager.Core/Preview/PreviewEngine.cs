namespace Emanager.Core.Preview;

public sealed record PreviewResult(string Path, string Kind, object? Value, bool IsPartial = false);
public interface IPreviewProvider { bool CanHandle(string path, string? mimeType); Task<PreviewResult?> PreviewAsync(string path, CancellationToken cancellationToken = default); }
public sealed class PreviewRegistry
{
    private readonly List<IPreviewProvider> _providers = new();
    public void Register(IPreviewProvider provider) => _providers.Add(provider);
    public IReadOnlyList<IPreviewProvider> Providers => _providers.AsReadOnly();
    public IPreviewProvider? Find(string path, string? mimeType) => _providers.FirstOrDefault(x => x.CanHandle(path, mimeType));
}
public sealed class PreviewCoordinator(PreviewRegistry registry)
{
    public async Task<PreviewResult?> PreviewAsync(string path, string? mimeType = null, CancellationToken cancellationToken = default) => registry.Find(path, mimeType) is { } provider ? await provider.PreviewAsync(path, cancellationToken) : null;
}
public sealed class TextPreviewProvider(int maxBytes = 1024 * 1024) : IPreviewProvider
{
    public bool CanHandle(string path, string? mimeType) => mimeType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) == true || new[] { ".txt", ".cs", ".json", ".xml", ".md" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    public async Task<PreviewResult?> PreviewAsync(string path, CancellationToken cancellationToken = default) { await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); var size = (int)Math.Min(maxBytes, stream.Length); var bytes = new byte[size]; var count = await stream.ReadAsync(bytes, cancellationToken); return new(path, "text", System.Text.Encoding.UTF8.GetString(bytes, 0, count), stream.Length > maxBytes); }
}
