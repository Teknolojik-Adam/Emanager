using System.Security.Cryptography;
using System.Text;

namespace Emanager.Core.Storage;

public sealed class DeviceDependencyResolver : IDeviceDependencyResolver
{
    public IReadOnlySet<string> GetAncestors(StorageDiscoverySnapshot snapshot, string stableId)
    {
        var nodes = snapshot.Nodes.ToDictionary(x => x.StableId, StringComparer.Ordinal);
        var result = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        if (nodes.ContainsKey(stableId)) pending.Push(stableId);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!result.Add(current) || !nodes.TryGetValue(current, out var node)) continue;
            foreach (var parent in node.ParentIds) pending.Push(parent);
        }
        return result;
    }

    public IReadOnlySet<string> GetDescendants(StorageDiscoverySnapshot snapshot, string stableId)
    {
        var nodes = snapshot.Nodes.ToDictionary(x => x.StableId, StringComparer.Ordinal);
        var result = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        if (nodes.ContainsKey(stableId)) pending.Push(stableId);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!result.Add(current) || !nodes.TryGetValue(current, out var node)) continue;
            foreach (var child in node.ChildIds) pending.Push(child);
        }
        return result;
    }
}

public sealed class SystemDiskProtectionService : ISystemDiskProtectionService
{
    private static readonly string[] CriticalMounts =
    ["/", "/boot", "/boot/efi", "/home", "/usr", "/var", "/var/lib", "/var/log"];
    private readonly IDeviceDependencyResolver dependencies;

    public SystemDiskProtectionService(IDeviceDependencyResolver? dependencies = null) =>
        this.dependencies = dependencies ?? new DeviceDependencyResolver();

    public DiskProtectionResult Evaluate(StorageDiscoverySnapshot snapshot, string stableId)
    {
        if (!snapshot.IsComplete)
            return Unknown(stableId, snapshot.Error ?? "Storage discovery is incomplete.");
        var node = snapshot.Nodes.FirstOrDefault(x => x.StableId == stableId);
        if (node is null || string.IsNullOrWhiteSpace(node.DevicePath))
            return Unknown(stableId, "The storage device identity could not be resolved.");
        if (node.Kind is StorageNodeKind.Unknown or StorageNodeKind.Loop or StorageNodeKind.Zram)
            return Unknown(stableId, "This device type is not safe to manage.");

        var protectedSources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mount in snapshot.Mounts.Where(x => IsCriticalMount(x.Target)))
            protectedSources.Add(mount.StableDeviceId);
        foreach (var swap in snapshot.Swaps.Where(x => x.IsActive))
            if (swap.StableDeviceId is { Length: > 0 } id) protectedSources.Add(id);

        var protectedClosure = new HashSet<string>(StringComparer.Ordinal);
        var protectedPhysical = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in protectedSources)
        {
            var chain = dependencies.GetAncestors(snapshot, source);
            protectedClosure.UnionWith(chain);
            foreach (var item in chain)
            {
                var ancestor = snapshot.Nodes.FirstOrDefault(x => x.StableId == item);
                if (ancestor?.Kind == StorageNodeKind.PhysicalDisk) protectedPhysical.Add(item);
            }
        }

        var chainForNode = dependencies.GetAncestors(snapshot, stableId);
        var isSystemCritical = protectedClosure.Contains(stableId);
        var sharesProtectedDisk = chainForNode.Any(protectedPhysical.Contains);
        var protectionClass = isSystemCritical
            ? DiskProtectionClass.SystemCritical
            : sharesProtectedDisk
                ? DiskProtectionClass.Protected
                : node.IsRemovable ? DiskProtectionClass.Removable : DiskProtectionClass.DataDisk;
        var reason = protectionClass switch
        {
            DiskProtectionClass.SystemCritical => "This device contains an active system mount or swap device.",
            DiskProtectionClass.Protected => "This device belongs to a physical disk that carries system storage.",
            DiskProtectionClass.Removable => "This device is removable and is not part of the running system.",
            _ => "This device is not part of the running system."
        };
        var filesystemNode = node.Kind is StorageNodeKind.Partition or StorageNodeKind.Filesystem or StorageNodeKind.CryptMapping or StorageNodeKind.LvmVolume or StorageNodeKind.RaidArray or StorageNodeKind.DeviceMapper;
        var descendants = dependencies.GetDescendants(snapshot, stableId);
        var hasMountedDescendant = descendants
            .Select(id => snapshot.Nodes.FirstOrDefault(x => x.StableId == id))
            .Any(x => x?.IsMounted == true);
        var capabilities = protectionClass == DiskProtectionClass.SystemCritical
            ? new DiskOperationCapabilities(false, false, false, false, reason)
            : new DiskOperationCapabilities(
                filesystemNode && !string.IsNullOrWhiteSpace(node.FileSystemType) && !node.IsMounted && !node.IsReadOnly,
                filesystemNode && node.IsMounted,
                // The App backend implements Eject through UDisks2's safe
                // power-off client operation, so both capabilities must be
                // present before exposing "Eject this disk".
                protectionClass == DiskProtectionClass.Removable && node.IsPhysicalDisk && node.CanEject && node.CanPowerOff && !hasMountedDescendant,
                protectionClass == DiskProtectionClass.Removable && node.IsPhysicalDisk && node.CanPowerOff && !hasMountedDescendant,
                protectionClass == DiskProtectionClass.Protected ? "The physical disk is protected; only this independent filesystem may be managed." : null);
        return new DiskProtectionResult(stableId, protectionClass, reason, capabilities, node);
    }

    private static bool IsCriticalMount(string target) =>
        CriticalMounts.Any(root => target == root || (root != "/" && target.StartsWith(root + "/", StringComparison.Ordinal)));

    private static DiskProtectionResult Unknown(string stableId, string reason) =>
        new(stableId, DiskProtectionClass.Unknown, reason,
            new DiskOperationCapabilities(false, false, false, false, reason), null);
}

public sealed class DiskOperationPolicy : IDiskOperationPolicy
{
    private readonly ISystemDiskProtectionService protection;
    public DiskOperationPolicy(ISystemDiskProtectionService? protection = null) =>
        this.protection = protection ?? new SystemDiskProtectionService();

    public DiskOperationAuthorization Authorize(StorageDiscoverySnapshot snapshot, DiskOperationRequest request)
    {
        if (!snapshot.IsComplete)
            return Denied(snapshot, request, DiskDenyReason.DiscoveryUnavailable, snapshot.Error ?? "Storage discovery is unavailable.");
        if (!string.IsNullOrWhiteSpace(request.ExpectedSnapshotFingerprint) &&
            !string.Equals(request.ExpectedSnapshotFingerprint, snapshot.Fingerprint, StringComparison.Ordinal))
            return Denied(snapshot, request, DiskDenyReason.TopologyChanged, "The storage topology changed; refresh and try again.");
        var result = protection.Evaluate(snapshot, request.StableId);
        if (result.Node is null)
            return Denied(snapshot, request, DiskDenyReason.UnknownTarget, result.Reason, result);
        if (request.ExpectedDevicePath is not null && !string.Equals(request.ExpectedDevicePath, result.Node.DevicePath, StringComparison.Ordinal))
            return Denied(snapshot, request, DiskDenyReason.IdentityChanged, "The device identity changed; the operation was stopped.", result);
        var allowed = request.Operation switch
        {
            DiskOperationKind.Mount => result.Capabilities.CanMount,
            DiskOperationKind.Unmount => result.Capabilities.CanUnmount,
            DiskOperationKind.Eject => result.Capabilities.CanEject,
            DiskOperationKind.PowerOff => result.Capabilities.CanPowerOff,
            _ => false
        };
        if (allowed) return new(true, null, result.Reason, request, result, snapshot);
        var deny = result.ProtectionClass is DiskProtectionClass.SystemCritical or DiskProtectionClass.Protected
            ? DiskDenyReason.ProtectedPhysicalAncestor
            : DiskDenyReason.UnsupportedOperation;
        return Denied(snapshot, request, deny, result.Reason, result);
    }

    private static DiskOperationAuthorization Denied(StorageDiscoverySnapshot snapshot, DiskOperationRequest request,
        DiskDenyReason reason, string message, DiskProtectionResult? result = null) =>
        new(false, reason, message, request, result, snapshot);
}

public sealed class PolicyEnforcedDiskMountService : IDiskMountService
{
    private readonly IBlockDeviceDiscovery discovery;
    private readonly IDiskOperationPolicy policy;
    private readonly IDiskOperationBackend backend;

    public PolicyEnforcedDiskMountService(IBlockDeviceDiscovery discovery, IDiskOperationPolicy policy, IDiskOperationBackend backend)
    {
        this.discovery = discovery;
        this.policy = policy;
        this.backend = backend;
    }

    public async Task<DiskOperationResult> ExecuteAsync(DiskOperationRequest request, CancellationToken cancellationToken = default)
    {
        var before = await discovery.DiscoverAsync(cancellationToken);
        var authorization = policy.Authorize(before, request);
        if (!authorization.Allowed) return new(false, authorization.Message, authorization.DenyReason);
        var current = await discovery.DiscoverAsync(cancellationToken);
        var refreshed = request with { ExpectedSnapshotFingerprint = before.Fingerprint };
        var revalidated = policy.Authorize(current, refreshed);
        if (!revalidated.Allowed) return new(false, revalidated.Message, revalidated.DenyReason);
        var node = revalidated.Protection?.Node;
        if (node is null) return new(false, "The storage device disappeared before the operation started.", DiskDenyReason.DeviceUnavailable);
        var result = await backend.ExecuteAsync(node, refreshed, cancellationToken);
        if (!result.Succeeded)
            return new(false, result.Error ?? "The storage operation failed.", result.IsBusy ? DiskDenyReason.DeviceBusy : null);
        var after = await discovery.DiscoverAsync(cancellationToken);
        if (!after.IsComplete)
            return new(false, "The storage topology could not be verified after the operation.", DiskDenyReason.DiscoveryUnavailable);
        var afterNode = after.Nodes.FirstOrDefault(x => x.StableId == node.StableId);
        if (request.Operation == DiskOperationKind.Mount && (afterNode is null || !afterNode.IsMounted))
            return new(false, "The device was not mounted successfully.", DiskDenyReason.DeviceUnavailable);
        if ((request.Operation is DiskOperationKind.Unmount or DiskOperationKind.Eject or DiskOperationKind.PowerOff) && afterNode?.IsMounted == true)
            return new(false, "The device is still mounted after the operation.", DiskDenyReason.DeviceBusy);
        return new(true, result.MountPoint ?? "Storage operation completed.", null, result.MountPoint);
    }
}
