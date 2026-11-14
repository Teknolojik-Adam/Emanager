namespace Emanager.Core.Storage;

public sealed record StorageDevice(string Id, string DevicePath, string? FileSystem, string? MountPoint, long Size, bool IsRemovable, bool IsReadOnly);
public interface IStorageDeviceManager { Task<IReadOnlyList<StorageDevice>> DiscoverAsync(CancellationToken cancellationToken = default); Task MountAsync(StorageDevice device, bool readOnly, CancellationToken cancellationToken = default); Task UnmountAsync(StorageDevice device, CancellationToken cancellationToken = default); Task EjectAsync(StorageDevice device, CancellationToken cancellationToken = default); }
public sealed class UnsupportedStorageDeviceManager(string reason) : IStorageDeviceManager
{
    public Task<IReadOnlyList<StorageDevice>> DiscoverAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<StorageDevice>>(Array.Empty<StorageDevice>());
    public Task MountAsync(StorageDevice device, bool readOnly, CancellationToken cancellationToken = default) => Task.FromException(new PlatformNotSupportedException(reason));
    public Task UnmountAsync(StorageDevice device, CancellationToken cancellationToken = default) => Task.FromException(new PlatformNotSupportedException(reason));
    public Task EjectAsync(StorageDevice device, CancellationToken cancellationToken = default) => Task.FromException(new PlatformNotSupportedException(reason));
}
