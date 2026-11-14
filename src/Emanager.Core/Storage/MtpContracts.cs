using Emanager.Core.Network;

namespace Emanager.Core.Storage;

public sealed record MtpDevice(string Id, string Name, bool Connected);
public interface IMtpProvider { Task<IReadOnlyList<MtpDevice>> DiscoverAsync(CancellationToken cancellationToken = default); IAsyncEnumerable<NetworkEntry> ListAsync(string deviceId, string path, CancellationToken cancellationToken = default); Task CopyToDeviceAsync(string localPath, string deviceId, string remotePath, IProgress<long>? progress = null, CancellationToken cancellationToken = default); }
