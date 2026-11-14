namespace Emanager.Core.Network;

public sealed record NetworkConnection(string Uri, string? DisplayName = null);
public sealed record NetworkEntry(string Name, string Uri, bool IsDirectory, long? Size = null);
public interface ICredentialStore
{
    Task SaveAsync(string key, string user, string secret, CancellationToken cancellationToken = default);
    Task<(string User, string Secret)?> GetAsync(string key, CancellationToken cancellationToken = default);
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);
}
public interface INetworkProvider
{
    string Scheme { get; }
    bool IsAvailable { get; }
    Task ConnectAsync(NetworkConnection connection, CancellationToken cancellationToken = default);
    Task DisconnectAsync(NetworkConnection connection, CancellationToken cancellationToken = default);
    IAsyncEnumerable<NetworkEntry> ListAsync(string uri, CancellationToken cancellationToken = default);
    Task CopyToAsync(string sourceUri, string destinationUri, IProgress<long>? progress = null, CancellationToken cancellationToken = default);
}
public sealed class UnsupportedNetworkProvider(string scheme, string reason) : INetworkProvider
{
    public string Scheme => scheme;
    public bool IsAvailable => false;
    public Task ConnectAsync(NetworkConnection connection, CancellationToken cancellationToken = default) => Task.FromException(new PlatformNotSupportedException(reason));
    public Task DisconnectAsync(NetworkConnection connection, CancellationToken cancellationToken = default) => Task.FromException(new PlatformNotSupportedException(reason));
    public async IAsyncEnumerable<NetworkEntry> ListAsync(string uri, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) { await Task.CompletedTask; if (cancellationToken.IsCancellationRequested) yield break; throw new PlatformNotSupportedException(reason); }
    public Task CopyToAsync(string sourceUri, string destinationUri, IProgress<long>? progress = null, CancellationToken cancellationToken = default) => Task.FromException(new PlatformNotSupportedException(reason));
}
