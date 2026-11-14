namespace Emanager.Core.Storage;

public enum StorageNodeKind
{
    PhysicalDisk,
    Partition,
    Filesystem,
    CryptContainer,
    CryptMapping,
    LvmPhysicalVolume,
    LvmVolume,
    RaidMember,
    RaidArray,
    DeviceMapper,
    Loop,
    Zram,
    Unknown
}

public enum DiskProtectionClass
{
    SystemCritical,
    Protected,
    DataDisk,
    Removable,
    Unknown
}

public enum DiskOperationKind
{
    Mount,
    Unmount,
    Eject,
    PowerOff
}

public enum DiskDenyReason
{
    UnknownTarget,
    DiscoveryUnavailable,
    UnknownTopology,
    UnresolvedIdentity,
    NotBlockDevice,
    ProtectedMount,
    ProtectedSwap,
    ProtectedPhysicalAncestor,
    UnsupportedOperation,
    IdentityChanged,
    TopologyChanged,
    DeviceBusy,
    DeviceUnavailable,
    AuthorizationDenied
}

public sealed record StorageNode(
    string StableId,
    StorageNodeKind Kind,
    string KernelName,
    string? DevicePath,
    long SizeBytes,
    bool IsRemovable,
    bool IsReadOnly,
    string? FileSystemType,
    string? FileSystemUuid,
    string? Label,
    string? Transport,
    IReadOnlyList<string> ParentIds,
    IReadOnlyList<string> ChildIds,
    IReadOnlyList<string> MountPoints,
    bool CanEject = false,
    bool CanPowerOff = false,
    string? Model = null,
    string? Serial = null)
{
    public bool IsMounted => MountPoints.Count > 0;
    public bool IsPhysicalDisk => Kind == StorageNodeKind.PhysicalDisk;
}

public sealed record StorageMount(
    string StableDeviceId,
    string Target,
    string Source,
    string FileSystemType,
    bool IsReadOnly,
    IReadOnlyList<string> Options);

public sealed record SwapEntry(string Path, string? StableDeviceId, bool IsActive);

public sealed record StorageDiscoverySnapshot(
    IReadOnlyList<StorageNode> Nodes,
    IReadOnlyList<StorageMount> Mounts,
    IReadOnlyList<SwapEntry> Swaps,
    bool IsComplete,
    string Fingerprint,
    DateTimeOffset CapturedAt,
    string? Error = null)
{
    public static StorageDiscoverySnapshot Unavailable(string error) =>
        new(Array.Empty<StorageNode>(), Array.Empty<StorageMount>(), Array.Empty<SwapEntry>(),
            false, "unavailable", DateTimeOffset.UtcNow, error);
}

public sealed record DiskOperationCapabilities(
    bool CanMount,
    bool CanUnmount,
    bool CanEject,
    bool CanPowerOff,
    string? Reason = null);

public sealed record DiskProtectionResult(
    string StableId,
    DiskProtectionClass ProtectionClass,
    string Reason,
    DiskOperationCapabilities Capabilities,
    StorageNode? Node);

public sealed record DiskOperationRequest(
    DiskOperationKind Operation,
    string StableId,
    string? ExpectedDevicePath = null,
    string? ExpectedSnapshotFingerprint = null,
    bool ReadOnly = false);

public sealed record DiskOperationAuthorization(
    bool Allowed,
    DiskDenyReason? DenyReason,
    string Message,
    DiskOperationRequest Request,
    DiskProtectionResult? Protection,
    StorageDiscoverySnapshot Snapshot);

public sealed record DiskOperationBackendResult(
    bool Succeeded,
    string? MountPoint = null,
    string? Error = null,
    bool IsBusy = false);

public sealed record DiskOperationResult(
    bool Succeeded,
    string Message,
    DiskDenyReason? DenyReason = null,
    string? MountPoint = null);

public interface IBlockDeviceDiscovery
{
    Task<StorageDiscoverySnapshot> DiscoverAsync(CancellationToken cancellationToken = default);
}

public interface IDeviceDependencyResolver
{
    IReadOnlySet<string> GetAncestors(StorageDiscoverySnapshot snapshot, string stableId);
    IReadOnlySet<string> GetDescendants(StorageDiscoverySnapshot snapshot, string stableId);
}

public interface ISystemDiskProtectionService
{
    DiskProtectionResult Evaluate(StorageDiscoverySnapshot snapshot, string stableId);
}

public interface IDiskOperationPolicy
{
    DiskOperationAuthorization Authorize(StorageDiscoverySnapshot snapshot, DiskOperationRequest request);
}

public interface IDiskOperationBackend
{
    Task<DiskOperationBackendResult> ExecuteAsync(StorageNode node, DiskOperationRequest request, CancellationToken cancellationToken = default);
}

public interface IDiskMountService
{
    Task<DiskOperationResult> ExecuteAsync(DiskOperationRequest request, CancellationToken cancellationToken = default);
}
